using backend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;

namespace backend.Mongo;

[ApiController]
[Authorize(Policy = "AdminOnly")]
[Route("api/admin")]
public sealed class MongoAdminController(MongoStore db) : ControllerBase
{
    [HttpPost("products")]
    [RequestSizeLimit(5_300_000)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> CreateProduct([FromForm] CreateProductRequest request, CancellationToken ct)
    {
        if (!Valid(request) || (request.ImageFile is null && (string.IsNullOrWhiteSpace(request.Image) || request.Image.Length > 200)))
            return BadRequest(new { message = "Enter product details, provide an image or emoji, use a positive price, and set valid stock." });
        if (!ValidImage(request.ImageFile)) return BadRequest(new { message = "Choose a JPG, PNG, GIF, or WebP image smaller than 5 MB." });
        var product = new Product
        {
            Name = request.Name.Trim(), Description = request.Description.Trim(), Price = decimal.Round(request.Price, 2),
            Category = request.Category.Trim(), Stock = request.Stock, Image = request.ImageFile is null ? request.Image!.Trim() : "",
            ImageData = request.ImageFile is null ? null : await ReadImage(request.ImageFile, ct), ImageContentType = request.ImageFile?.ContentType
        };
        await db.Transact(async (session, token) =>
        {
            product.Id = await db.NextId("product", session, token);
            await db.Products.InsertOneAsync(session, new MongoEntity<Product> { Id = product.Id, Value = product }, cancellationToken: token);
            return true;
        }, ct);
        return Created("/api/products", product);
    }

    [HttpPut("products/{id:int}")]
    [RequestSizeLimit(5_300_000)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UpdateProduct(int id, [FromForm] CreateProductRequest request, CancellationToken ct)
    {
        if (!ValidImage(request.ImageFile)) return BadRequest(new { message = "Choose a JPG, PNG, GIF, or WebP image smaller than 5 MB." });
        var current = (await db.Products.Find(x => x.Id == id).FirstOrDefaultAsync(ct))?.Value;
        if (current is null) return NotFound(new { message = "Product not found." });
        if (!Valid(request) || request.Image is { Length: > 200 } ||
            (request.ImageFile is null && string.IsNullOrWhiteSpace(request.Image) && current.ImageData is null))
            return BadRequest(new { message = "Enter valid product details, a positive price, stock, and an image or emoji." });
        var update = Builders<MongoEntity<Product>>.Update.Set(x => x.Value.Name, request.Name.Trim())
            .Set(x => x.Value.Description, request.Description.Trim()).Set(x => x.Value.Price, decimal.Round(request.Price, 2))
            .Set(x => x.Value.Category, request.Category.Trim()).Set(x => x.Value.Stock, request.Stock);
        if (request.ImageFile is { } file)
            update = update.Set(x => x.Value.Image, "").Set(x => x.Value.ImageData, await ReadImage(file, ct))
                .Set(x => x.Value.ImageContentType, file.ContentType);
        else if (!string.IsNullOrWhiteSpace(request.Image))
            update = update.Set(x => x.Value.Image, request.Image.Trim()).Set(x => x.Value.ImageData, null)
                .Set(x => x.Value.ImageContentType, null);
        // Guard against overwriting a checkout's stock deduction or a concurrent admin edit.
        var result = await db.Products.FindOneAndUpdateAsync(x => x.Id == id && x.Value.Stock == current.Stock,
            update, new FindOneAndUpdateOptions<MongoEntity<Product>> { ReturnDocument = ReturnDocument.After }, ct);
        if (result is null) return Conflict(new { message = "Product stock changed. Refresh and try again." });
        var product = result.Value;
        return Ok(new { product.Id, product.Name, product.Description, product.Price, product.Image,
            imageUrl = product.ImageData is null ? null : $"{Request.Scheme}://{Request.Host}/api/products/{product.Id}/image?v={DateTime.UtcNow.Ticks}",
            product.Category, product.Stock });
    }

    [HttpDelete("products/{id:int}")]
    public async Task<IActionResult> DeleteProduct(int id, CancellationToken ct) =>
        (await db.Products.DeleteOneAsync(x => x.Id == id, ct)).DeletedCount == 0
            ? NotFound(new { message = "Product not found." }) : NoContent();

    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary(CancellationToken ct)
    {
        var revenue = await db.Orders.Aggregate()
            .Match(x => x.Value.PaymentStatus == "Paid")
            .Group(x => 1, group => new { Total = group.Sum(x => x.Value.Total) })
            .FirstOrDefaultAsync(ct);
        return Ok(new
        {
            totalOrders = await db.Orders.CountDocumentsAsync(FilterDefinition<MongoEntity<Order>>.Empty, cancellationToken: ct),
            revenue = revenue?.Total ?? 0m,
            pendingPayments = await db.Orders.CountDocumentsAsync(x => x.Value.PaymentStatus == "Pending", cancellationToken: ct),
            productCount = await db.Products.CountDocumentsAsync(FilterDefinition<MongoEntity<Product>>.Empty, cancellationToken: ct)
        });
    }

    [HttpPost("orders/{id:int}/payment-status")]
    public async Task<IActionResult> UpdatePaymentStatus(int id, UpdatePaymentStatusRequest request, CancellationToken ct)
    {
        var allowed = new[] { "Pending", "Paid", "Failed", "Refunded" };
        if (!allowed.Contains(request.PaymentStatus, StringComparer.OrdinalIgnoreCase))
            return BadRequest(new { message = "Payment status must be Pending, Paid, Failed, or Refunded." });
        var next = allowed.First(x => x.Equals(request.PaymentStatus, StringComparison.OrdinalIgnoreCase));
        try
        {
            var order = await db.Transact(async (session, token) =>
            {
                var existing = (await db.Orders.Find(session, x => x.Id == id).FirstOrDefaultAsync(token))?.Value;
                if (existing is null) return (Order?)null;
                if (existing.PaymentMethod == "Razorpay" && next != "Pending" ||
                    existing.PaymentStatus is "Failed" or "Refunded" or "Expired" or "Cancelled" ||
                    existing.PaymentStatus == "Paid" && next != "Refunded")
                    throw new MongoConflictException();
                if (next == "Refunded" && existing.PaymentStatus != "Paid" ||
                    next == "Paid" && (existing.PaymentStatus != "Pending" || string.IsNullOrWhiteSpace(existing.PaymentReference) || existing.PaymentReportedAt is null))
                    throw new ArgumentException("This order requires a reported payment before verification, or a Paid status before refund.");
                var reviewed = existing.PaymentStatus == "Pending" && existing.PaymentReportedAt is not null && next is "Paid" or "Failed";
                var update = Builders<MongoEntity<Order>>.Update.Set(x => x.Value.PaymentStatus, next);
                if (next == "Pending")
                    update = update.Set(x => x.Value.PaymentReportedAt, null).Set(x => x.Value.PaymentReference, null)
                        .Set(x => x.Value.PaymentVerifiedAt, null).Set(x => x.Value.PaymentVerifiedBy, null);
                else if (reviewed)
                    update = update.Set(x => x.Value.PaymentVerifiedAt, DateTime.UtcNow).Set(x => x.Value.PaymentVerifiedBy, User.Identity?.Name);
                var filter = Builders<MongoEntity<Order>>.Filter.Where(x => x.Id == id && x.Value.PaymentStatus == existing.PaymentStatus);
                if (reviewed) filter &= Builders<MongoEntity<Order>>.Filter.Ne(x => x.Value.PaymentReportedAt, null);
                var changed = await db.Orders.UpdateOneAsync(session, filter, update, cancellationToken: token);
                if (changed.MatchedCount != 1) throw new MongoConflictException();
                if (next == "Failed" && existing.PaymentStatus == "Pending")
                    foreach (var item in existing.Items)
                        await db.Products.UpdateOneAsync(session, x => x.Id == item.ProductId,
                            Builders<MongoEntity<Product>>.Update.Inc(x => x.Value.Stock, item.Quantity), cancellationToken: token);
                return (await db.Orders.Find(session, x => x.Id == id).FirstAsync(token)).Value;
            }, ct);
            return order is null ? NotFound() : Ok(order);
        }
        catch (MongoConflictException) { return Conflict(new { message = "Order payment status changed or is in a terminal state. Refresh and try again." }); }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("orders/{id:int}/fulfillment")]
    public async Task<IActionResult> UpdateFulfillment(int id, UpdateFulfillmentRequest request, CancellationToken ct)
    {
        if (request.Status is not ("Order received" or "Dispatched" or "Delivered"))
            return BadRequest(new { message = "Choose Order received, Dispatched or Delivered." });
        var order = (await db.Orders.Find(x => x.Id == id).FirstOrDefaultAsync(ct))?.Value;
        if (order is null) return NotFound();
        if (order.PaymentStatus != "Paid") return Conflict(new { message = "Only paid orders can be dispatched or delivered." });
        if (order.FulfillmentStatus == "Order received" && request.Status != "Dispatched" ||
            order.FulfillmentStatus == "Dispatched" && request.Status != "Delivered" || order.FulfillmentStatus == "Delivered")
            return Conflict(new { message = "Fulfillment status must advance from received to dispatched to delivered." });
        var tracking = request.Status == "Dispatched" ? request.TrackingId?.Trim() : order.TrackingId;
        if (request.Status == "Dispatched" && (string.IsNullOrWhiteSpace(tracking) || tracking.Length > 100))
            return BadRequest(new { message = "Enter a tracking ID of up to 100 characters before dispatching." });
        var updated = await db.Orders.UpdateOneAsync(x => x.Id == id && x.Value.PaymentStatus == "Paid" &&
            x.Value.FulfillmentStatus == order.FulfillmentStatus,
            Builders<MongoEntity<Order>>.Update.Set(x => x.Value.FulfillmentStatus, request.Status).Set(x => x.Value.TrackingId, tracking), cancellationToken: ct);
        return updated.ModifiedCount == 0 ? Conflict(new { message = "Order status changed. Refresh and try again." }) :
            Ok(new { fulfillmentStatus = request.Status, trackingId = tracking });
    }

    private static bool Valid(CreateProductRequest request) =>
        !string.IsNullOrWhiteSpace(request.Name) && request.Name.Length <= 120 &&
        !string.IsNullOrWhiteSpace(request.Description) && request.Description.Length <= 1000 &&
        !string.IsNullOrWhiteSpace(request.Category) && request.Category.Length <= 60 &&
        request.Price > 0 && request.Price <= 99999999.99m && request.Stock is >= 0 and <= 1000000;
    private static bool ValidImage(IFormFile? file) => file is null ||
        file.Length is > 0 and <= 5 * 1024 * 1024 && file.ContentType is "image/jpeg" or "image/png" or "image/gif" or "image/webp";
    private static async Task<byte[]> ReadImage(IFormFile file, CancellationToken ct)
    {
        await using var stream = new MemoryStream((int)file.Length);
        await file.CopyToAsync(stream, ct);
        return stream.ToArray();
    }
}