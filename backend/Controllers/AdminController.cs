using backend.Data;
using backend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace backend.Controllers;

[ApiController]
[Authorize(Policy = "AdminOnly")]
[Route("api/admin")]
public sealed class AdminController(StoreDbContext db) : ControllerBase
{
    [HttpPost("products")]
    [RequestSizeLimit(5_300_000)]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<Product>> CreateProduct([FromForm] CreateProductRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 120 ||
            string.IsNullOrWhiteSpace(request.Description) || request.Description.Length > 1000 ||
            (request.ImageFile is null && (string.IsNullOrWhiteSpace(request.Image) || request.Image.Length > 200)) ||
            string.IsNullOrWhiteSpace(request.Category) || request.Category.Length > 60 ||
            request.Price <= 0 || request.Price > 99999999.99m ||
            request.Stock < 0 || request.Stock > 1000000)
        {
            return BadRequest(new { message = "Enter product details, provide an image or emoji, use a positive price, and set valid stock." });
        }

        if (request.ImageFile is { } file &&
            (file.Length == 0 || file.Length > 5 * 1024 * 1024 || !IsSupportedImage(file.ContentType)))
        {
            return BadRequest(new { message = "Choose a JPG, PNG, GIF, or WebP image smaller than 5 MB." });
        }

        var product = new Product
        {
            Name = request.Name.Trim(),
            Description = request.Description.Trim(),
            Price = decimal.Round(request.Price, 2),
            Image = request.ImageFile is null ? request.Image!.Trim() : "",
            ImageData = request.ImageFile is null ? null : await ReadImageAsync(request.ImageFile, cancellationToken),
            ImageContentType = request.ImageFile?.ContentType,
            Category = request.Category.Trim(),
            Stock = request.Stock
        };

        db.Products.Add(product);
        await db.SaveChangesAsync(cancellationToken);
        return Created("/api/products", product);
    }

    private static bool IsSupportedImage(string contentType) => contentType is
        "image/jpeg" or "image/png" or "image/gif" or "image/webp";

    private static async Task<byte[]> ReadImageAsync(IFormFile file, CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream((int)file.Length);
        await file.CopyToAsync(stream, cancellationToken);
        return stream.ToArray();
    }

    [HttpDelete("products/{id:int}")]
    public async Task<IActionResult> DeleteProduct(int id, CancellationToken cancellationToken)
    {
        var product = await db.Products.FindAsync(new object[] { id }, cancellationToken);
        if (product is null)
        {
            return NotFound(new { message = "Product not found." });
        }

        db.Products.Remove(product);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary(CancellationToken cancellationToken)
    {
        var orders = await db.Orders.AsNoTracking().ToListAsync(cancellationToken);
        return Ok(new
        {
            totalOrders = orders.Count,
            revenue = orders.Where(order => order.PaymentStatus == "Paid").Sum(order => order.Total),
            pendingPayments = orders.Count(order => order.PaymentStatus == "Pending"),
            productCount = await db.Products.CountAsync(cancellationToken)
        });
    }

    [HttpPost("orders/{id:int}/payment-status")]
    public async Task<IActionResult> UpdatePaymentStatus(int id, UpdatePaymentStatusRequest request, CancellationToken cancellationToken)
    {
        var allowed = new[] { "Pending", "Paid", "Failed", "Refunded" };
        if (!allowed.Contains(request.PaymentStatus, StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest(new { message = "Payment status must be Pending, Paid, Failed, or Refunded." });
        }

        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (order is null)
        {
            return NotFound();
        }

        var nextStatus = allowed.First(status => status.Equals(request.PaymentStatus, StringComparison.OrdinalIgnoreCase));
        if (order.PaymentStatus is "Failed" or "Refunded" or "Expired" or "Cancelled" ||
            (order.PaymentStatus == "Paid" && nextStatus != "Refunded"))
        {
            return Conflict(new { message = "This order is already in a terminal payment state and cannot be changed here." });
        }
        if (nextStatus == "Refunded" && order.PaymentStatus != "Paid")
        {
            return BadRequest(new { message = "Only an order previously verified as Paid can be marked Refunded." });
        }
        if (nextStatus == "Paid" && (order.PaymentStatus != "Pending" ||
            string.IsNullOrWhiteSpace(order.PaymentReference) || order.PaymentReportedAt is null))
        {
            return BadRequest(new { message = "This order needs a customer UTR/reference report before it can be verified as Paid." });
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var reviewedReportedPayment = order.PaymentStatus == "Pending" && order.PaymentReportedAt is not null &&
            (nextStatus == "Paid" || nextStatus == "Failed");
        int updatedRows;
        if (nextStatus == "Pending")
        {
            updatedRows = await db.Orders.Where(candidate => candidate.Id == id && candidate.PaymentStatus == order.PaymentStatus)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(candidate => candidate.PaymentStatus, "Pending")
                    .SetProperty(candidate => candidate.PaymentReportedAt, (DateTime?)null)
                    .SetProperty(candidate => candidate.PaymentReference, (string?)null)
                    .SetProperty(candidate => candidate.PaymentVerifiedAt, (DateTime?)null)
                    .SetProperty(candidate => candidate.PaymentVerifiedBy, (string?)null), cancellationToken);
        }
        else if (reviewedReportedPayment)
        {
            updatedRows = await db.Orders.Where(candidate => candidate.Id == id && candidate.PaymentStatus == "Pending" && candidate.PaymentReportedAt != null)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(candidate => candidate.PaymentStatus, nextStatus)
                    .SetProperty(candidate => candidate.PaymentVerifiedAt, (DateTime?)now)
                    .SetProperty(candidate => candidate.PaymentVerifiedBy, User.Identity!.Name), cancellationToken);
        }
        else
        {
            updatedRows = await db.Orders.Where(candidate => candidate.Id == id && candidate.PaymentStatus == order.PaymentStatus)
                .ExecuteUpdateAsync(update => update.SetProperty(candidate => candidate.PaymentStatus, nextStatus), cancellationToken);
        }

        if (updatedRows == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict(new { message = "The order payment status changed. Refresh the order and try again." });
        }

        if (nextStatus == "Failed" && order.PaymentStatus == "Pending")
        {
            var items = await db.OrderItems.AsNoTracking()
                .Where(item => item.OrderId == id)
                .ToListAsync(cancellationToken);
            var productIds = items.Select(item => item.ProductId).Distinct().ToArray();
            var products = await db.Products.Where(product => productIds.Contains(product.Id))
                .ToDictionaryAsync(product => product.Id, cancellationToken);
            foreach (var item in items)
            {
                if (products.TryGetValue(item.ProductId, out var product))
                {
                    product.Stock += item.Quantity;
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        order = await db.Orders.AsNoTracking().Include(candidate => candidate.Items)
            .SingleAsync(candidate => candidate.Id == id, cancellationToken);
        return Ok(order);
    }
}
