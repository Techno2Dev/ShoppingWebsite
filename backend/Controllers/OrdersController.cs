using backend.Data;
using backend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class OrdersController(StoreDbContext db) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<IEnumerable<Order>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await db.Orders.AsNoTracking().Include(order => order.Items)
            .OrderByDescending(order => order.CreatedAt).ToListAsync(cancellationToken));

    [HttpGet("{id:int}")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<Order>> GetById(int id, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking().Include(item => item.Items)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        return order is null ? NotFound() : Ok(order);
    }

    [HttpPost]
    public async Task<ActionResult<CreateOrderResponse>> Create(CreateOrderRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CustomerName) || string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrWhiteSpace(request.Address) || request.Items.Count == 0 ||
            request.Items.Any(item => item.Quantity < 1 || item.Quantity > 99))
        {
            return BadRequest(new { message = "Enter customer details and at least one valid item." });
        }

        var requestedItems = request.Items
            .GroupBy(item => item.ProductId)
            .Select(group => new CreateOrderItemRequest { ProductId = group.Key, Quantity = group.Sum(item => item.Quantity) })
            .ToList();
        if (requestedItems.Any(item => item.Quantity > 99))
        {
            return BadRequest(new { message = "The total quantity per product cannot exceed 99." });
        }

        var ids = requestedItems.Select(item => item.ProductId).ToArray();
        var products = await db.Products.Where(product => ids.Contains(product.Id)).ToDictionaryAsync(product => product.Id, cancellationToken);
        if (products.Count != ids.Length || requestedItems.Any(item => products[item.ProductId].Stock < item.Quantity))
        {
            return Conflict(new { message = "One or more products are unavailable in the requested quantity." });
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var accessToken = OrderPaymentSecurity.CreateAccessToken();
        var order = new Order
        {
            CustomerName = request.CustomerName.Trim(),
            Email = request.Email.Trim(),
            Address = request.Address.Trim(),
            PaymentStatus = "Pending",
            PaymentAccessTokenHash = OrderPaymentSecurity.HashAccessToken(accessToken),
            Items = requestedItems.Select(item => new OrderItem
            {
                ProductId = item.ProductId,
                ProductName = products[item.ProductId].Name,
                Quantity = item.Quantity,
                UnitPrice = products[item.ProductId].Price
            }).ToList()
        };
        order.Total = order.Items.Sum(item => item.UnitPrice * item.Quantity);

        foreach (var item in requestedItems)
        {
            products[item.ProductId].Stock -= item.Quantity;
        }

        db.Orders.Add(order);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var response = new CreateOrderResponse(
            order.Id,
            order.Total,
            order.CreatedAt,
            order.PaymentStatus,
            order.Items,
            accessToken);
        return Created($"/api/orders/{order.Id}/payment-status", response);
    }

    [AllowAnonymous]
    [HttpPost("{id:int}/payment-status")]
    public async Task<ActionResult<CustomerOrderStatusResponse>> GetCustomerPaymentStatus(
        int id,
        ReportUpiPaymentRequest request,
        CancellationToken cancellationToken)
    {
        var order = await db.Orders.Include(candidate => candidate.Items)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (order is null || !HasValidAccessToken(order, request.AccessToken))
        {
            return NotFound();
        }

        return Ok(ToCustomerStatus(order));
    }

    [AllowAnonymous]
    [HttpPost("{id:int}/payment-reported")]
    public async Task<ActionResult<CustomerOrderStatusResponse>> ReportPayment(
        int id,
        ReportUpiPaymentRequest request,
        CancellationToken cancellationToken)
    {
        var order = await db.Orders.Include(candidate => candidate.Items)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (order is null || !HasValidAccessToken(order, request.AccessToken))
        {
            return NotFound();
        }

        if (order.PaymentStatus != "Pending")
        {
            return Conflict(new { message = "An admin has already changed this order's payment status." });
        }
        var transactionReference = request.TransactionReference?.Trim();
        if (string.IsNullOrWhiteSpace(transactionReference) || transactionReference.Length is < 6 or > 100)
        {
            return BadRequest(new { message = "Enter a UPI transaction reference between 6 and 100 characters." });
        }

        var reportedAt = DateTime.UtcNow;
        var paymentReference = transactionReference;
        var updatedRows = await db.Orders
            .Where(candidate => candidate.Id == id && candidate.PaymentStatus == "Pending")
            .ExecuteUpdateAsync(update => update
                .SetProperty(candidate => candidate.PaymentReference, paymentReference)
                .SetProperty(candidate => candidate.PaymentReportedAt, (DateTime?)reportedAt), cancellationToken);
        if (updatedRows == 0)
        {
            return Conflict(new { message = "An admin has already changed this order's payment status." });
        }

        await db.Entry(order).ReloadAsync(cancellationToken);
        return Ok(ToCustomerStatus(order));
    }

    [AllowAnonymous]
    [HttpPost("{id:int}/cancel")]
    public async Task<ActionResult<CustomerOrderStatusResponse>> Cancel(
        int id,
        ReportUpiPaymentRequest request,
        CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking().Include(candidate => candidate.Items)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (order is null || !HasValidAccessToken(order, request.AccessToken))
        {
            return NotFound();
        }

        if (order.PaymentStatus != "Pending" || order.PaymentReportedAt is not null ||
            !string.IsNullOrWhiteSpace(order.PaymentReference))
        {
            return Conflict(new { message = "This order cannot be cancelled online. If you have already paid, contact the store." });
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var updatedRows = await db.Orders.Where(candidate => candidate.Id == id &&
                candidate.PaymentStatus == "Pending" && candidate.PaymentReportedAt == null && candidate.PaymentReference == null)
            .ExecuteUpdateAsync(update => update.SetProperty(candidate => candidate.PaymentStatus, "Cancelled"), cancellationToken);
        if (updatedRows == 0)
        {
            return Conflict(new { message = "The order changed. Refresh its payment status; if you paid, contact the store." });
        }

        foreach (var item in order.Items)
        {
            await db.Products.Where(product => product.Id == item.ProductId)
                .ExecuteUpdateAsync(update => update.SetProperty(product => product.Stock,
                    product => product.Stock + item.Quantity), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return Ok(ToCustomerStatus(order) with { PaymentStatus = "Cancelled" });
    }

    private static bool HasValidAccessToken(Order order, string accessToken)
    {
        if (string.IsNullOrWhiteSpace(order.PaymentAccessTokenHash) || string.IsNullOrWhiteSpace(accessToken))
        {
            return false;
        }

        var suppliedHash = OrderPaymentSecurity.HashAccessToken(accessToken);
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(suppliedHash),
            Convert.FromHexString(order.PaymentAccessTokenHash));
    }

    private static CustomerOrderStatusResponse ToCustomerStatus(Order order) => new(
        order.Id,
        order.Total,
        order.CreatedAt,
        order.PaymentStatus,
        order.PaymentReference,
        order.PaymentReportedAt,
        order.Items);
}
