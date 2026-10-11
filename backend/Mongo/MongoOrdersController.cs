using System.Security.Claims;
using System.Security.Cryptography;
using backend.Data;
using backend.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;

namespace backend.Mongo;

[ApiController]
[Route("api/orders")]
public sealed class MongoOrdersController(MongoStore db, TelegramOrderNotifier telegram, RazorpayTestGateway razorpay) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> GetAll([FromQuery] int page, CancellationToken ct)
    {
        if (page < 1 || page > 1000000) return BadRequest(new { message = "Page must be between 1 and 1000000." });
        const int pageSize = 15;
        var count = await db.Orders.CountDocumentsAsync(FilterDefinition<MongoEntity<Order>>.Empty, cancellationToken: ct);
        var orders = await db.Orders.Find(FilterDefinition<MongoEntity<Order>>.Empty)
            .Sort(Builders<MongoEntity<Order>>.Sort.Descending(x => x.Value.CreatedAt).Descending(x => x.Id))
            .Skip((page - 1) * pageSize).Limit(pageSize).ToListAsync(ct);
        return Ok(new { items = orders.Select(x => x.Value), totalCount = count, page, pageSize });
    }

    [HttpGet("{id:int}")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var order = (await db.Orders.Find(x => x.Id == id).FirstOrDefaultAsync(ct))?.Value;
        return order is null ? NotFound() : Ok(order);
    }

    [HttpGet("mine")]
    [Authorize(Policy = "CustomerOnly")]
    public async Task<IActionResult> Mine([FromQuery] int page = 1, CancellationToken ct = default)
    {
        if (page < 1 || page > 1000000) return BadRequest(new { message = "Invalid page." });
        const int pageSize = 20;
        var id = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var count = await db.Orders.CountDocumentsAsync(x => x.Value.CustomerUserId == id, cancellationToken: ct);
        var orders = await db.Orders.Find(x => x.Value.CustomerUserId == id)
            .Sort(Builders<MongoEntity<Order>>.Sort.Descending(x => x.Value.CreatedAt).Descending(x => x.Id))
            .Skip((page - 1) * pageSize).Limit(pageSize).ToListAsync(ct);
        Response.Headers.CacheControl = "no-store";
        return Ok(new { items = orders.Select(x => new
        {
            x.Value.Id, x.Value.CustomerName, x.Value.Email, x.Value.MobileNumber, x.Value.Address,
            x.Value.CreatedAt, x.Value.Total, x.Value.PaymentMethod, x.Value.PaymentStatus,
            x.Value.FulfillmentStatus, x.Value.TrackingId, x.Value.Items
        }), totalCount = count, page, pageSize });
    }

    [HttpPost("{id:int}/claim")]
    [Authorize(Policy = "CustomerOnly")]
    public async Task<IActionResult> Claim(int id, ReportUpiPaymentRequest request, CancellationToken ct)
    {
        var order = (await db.Orders.Find(x => x.Id == id).FirstOrDefaultAsync(ct))?.Value;
        if (order is null || !HasToken(order, request.AccessToken)) return NotFound();
        var customer = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (order.CustomerUserId == customer) return Ok();
        if (order.CustomerUserId is not null) return Conflict(new { message = "This order is already linked to an account." });
        var changed = await db.Orders.UpdateOneAsync(x => x.Id == id && x.Value.CustomerUserId == null,
            Builders<MongoEntity<Order>>.Update.Set(x => x.Value.CustomerUserId, customer), cancellationToken: ct);
        return changed.ModifiedCount == 1 ? Ok() : Conflict(new { message = "The order was linked to another account." });
    }

    [HttpPost]
    public async Task<ActionResult<CreateOrderResponse>> Create(CreateOrderRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CustomerName) || string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrWhiteSpace(request.Address) || request.Items.Count == 0 ||
            request.Items.Any(item => item.Quantity < 1 || item.Quantity > 99))
            return BadRequest(new { message = "Enter customer details and at least one valid item." });
        if (!IndianMobileNumber.TryNormalize(request.MobileNumber, out var mobile))
            return BadRequest(new { message = "Enter a valid 10-digit Indian mobile number." });
        if (request.PaymentMethod is not ("UPI" or "Razorpay")) return BadRequest(new { message = "Choose UPI or Razorpay." });
        if (request.PaymentMethod == "Razorpay" && !razorpay.Enabled)
            return BadRequest(new { message = "Razorpay test payments are not configured. Choose UPI instead." });
        var requested = request.Items.GroupBy(x => x.ProductId)
            .Select(x => new CreateOrderItemRequest { ProductId = x.Key, Quantity = x.Sum(y => y.Quantity) }).ToList();
        if (requested.Any(x => x.Quantity > 99))
            return BadRequest(new { message = "The total quantity per product cannot exceed 99." });
        int? customerId = null;
        if (Request.Headers.Authorization.Count > 0)
        {
            var auth = await HttpContext.AuthenticateAsync("CustomerBearer");
            if (!auth.Succeeded) return Unauthorized(new { message = "Customer session expired. Sign in again or continue as a guest." });
            customerId = int.Parse(auth.Principal!.FindFirstValue(ClaimTypes.NameIdentifier)!);
        }
        var accessToken = OrderPaymentSecurity.CreateAccessToken();
        try
        {
            var order = await db.Transact(async (session, cancellation) =>
            {
                var ids = requested.Select(x => x.ProductId).ToArray();
                var products = (await db.Products.Find(session, x => ids.Contains(x.Id)).ToListAsync(cancellation))
                    .ToDictionary(x => x.Id, x => x.Value);
                if (products.Count != ids.Length || requested.Any(x => products[x.ProductId].Stock < x.Quantity))
                    throw new MongoConflictException();
                var created = new Order
                {
                    Id = await db.NextId("order", session, cancellation), CustomerName = request.CustomerName.Trim(),
                    Email = request.Email.Trim(), MobileNumber = mobile, Address = request.Address.Trim(),
                    PaymentMethod = request.PaymentMethod, CustomerUserId = customerId,
                    PaymentAccessTokenHash = OrderPaymentSecurity.HashAccessToken(accessToken),
                    Items = requested.Select(x => new OrderItem { ProductId = x.ProductId,
                        ProductName = products[x.ProductId].Name, Quantity = x.Quantity, UnitPrice = products[x.ProductId].Price }).ToList()
                };
                created.Total = created.Items.Sum(x => x.UnitPrice * x.Quantity);
                if (created.PaymentMethod == "Razorpay" && created.Total < 1m)
                    throw new ArgumentException("Razorpay requires an order of at least ₹1.");
                foreach (var item in created.Items)
                {
                    item.Id = await db.NextId("orderItem", session, cancellation);
                    item.OrderId = created.Id;
                }
                foreach (var item in requested)
                {
                    var changed = await db.Products.UpdateOneAsync(session,
                        x => x.Id == item.ProductId && x.Value.Stock >= item.Quantity,
                        Builders<MongoEntity<Product>>.Update.Inc(x => x.Value.Stock, -item.Quantity), cancellationToken: cancellation);
                    if (changed.ModifiedCount != 1) throw new MongoConflictException();
                }
                await db.Orders.InsertOneAsync(session, new MongoEntity<Order> { Id = created.Id, Value = created }, cancellationToken: cancellation);
                return created;
            }, ct);
            await telegram.OrderCreatedAsync(order.Id, order.Total);
            return Created($"/api/orders/{order.Id}/payment-status", new CreateOrderResponse(order.Id, order.Total,
                order.CreatedAt, order.PaymentStatus, order.Items, accessToken, order.PaymentMethod, order.MobileNumber));
        }
        catch (MongoConflictException) { return Conflict(new { message = "One or more products are unavailable in the requested quantity." }); }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [AllowAnonymous]
    [HttpPost("{id:int}/payment-status")]
    public async Task<IActionResult> GetCustomerPaymentStatus(int id, ReportUpiPaymentRequest request, CancellationToken ct)
    {
        var order = (await db.Orders.Find(x => x.Id == id).FirstOrDefaultAsync(ct))?.Value;
        if (order is null || !HasToken(order, request.AccessToken)) return NotFound();
        if (order.PaymentMethod == "Razorpay" && order.PaymentStatus == "Pending" && order.RazorpayOrderId is not null)
        {
            try
            {
                var captured = await razorpay.FindCapturedPaymentAsync(order.RazorpayOrderId, order.Total, ct);
                if (captured is not null)
                {
                    await Capture(db, order, captured, ct);
                    order = (await db.Orders.Find(x => x.Id == id).FirstAsync(ct)).Value;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { }
        }
        return Ok(ToStatus(order));
    }

    [AllowAnonymous]
    [HttpPost("{id:int}/payment-reported")]
    public async Task<IActionResult> ReportPayment(int id, ReportUpiPaymentRequest request, CancellationToken ct)
    {
        var order = (await db.Orders.Find(x => x.Id == id).FirstOrDefaultAsync(ct))?.Value;
        if (order is null || !HasToken(order, request.AccessToken)) return NotFound();
        if (order.PaymentMethod != "UPI" || order.PaymentStatus != "Pending")
            return Conflict(new { message = "An admin has already changed this order's payment status." });
        var reference = request.TransactionReference?.Trim();
        if (string.IsNullOrWhiteSpace(reference) || reference.Length is < 6 or > 100)
            return BadRequest(new { message = "Enter a UPI transaction reference between 6 and 100 characters." });
        var updated = await db.Orders.UpdateOneAsync(x => x.Id == id && x.Value.PaymentMethod == "UPI" && x.Value.PaymentStatus == "Pending",
            Builders<MongoEntity<Order>>.Update.Set(x => x.Value.PaymentReference, reference)
                .Set(x => x.Value.PaymentReportedAt, DateTime.UtcNow), cancellationToken: ct);
        if (updated.MatchedCount == 0) return Conflict(new { message = "An admin has already changed this order's payment status." });
        order = (await db.Orders.Find(x => x.Id == id).FirstAsync(ct)).Value;
        await telegram.PaymentReportedAsync(id);
        return Ok(ToStatus(order));
    }

    [AllowAnonymous]
    [HttpPost("{id:int}/cancel")]
    public async Task<IActionResult> Cancel(int id, ReportUpiPaymentRequest request, CancellationToken ct)
    {
        var order = (await db.Orders.Find(x => x.Id == id).FirstOrDefaultAsync(ct))?.Value;
        if (order is null || !HasToken(order, request.AccessToken)) return NotFound();
        if (order.PaymentStatus != "Pending" || order.RazorpayOrderId is not null || order.PaymentReportedAt is not null ||
            !string.IsNullOrWhiteSpace(order.PaymentReference))
            return Conflict(new { message = "This order cannot be cancelled online. If you have already paid, contact the store." });
        try
        {
            var cancelled = await db.Transact(async (session, cancellation) =>
            {
                var current = (await db.Orders.Find(session, x => x.Id == id).FirstAsync(cancellation)).Value;
                var filter = Builders<MongoEntity<Order>>.Filter.Where(x => x.Id == id && x.Value.PaymentStatus == "Pending" &&
                    x.Value.RazorpayOrderId == null && x.Value.PaymentReportedAt == null && x.Value.PaymentReference == null &&
                    (x.CheckoutLeaseUntil == null || x.CheckoutLeaseUntil <= DateTime.UtcNow));
                var changed = await db.Orders.UpdateOneAsync(session, filter,
                    Builders<MongoEntity<Order>>.Update.Set(x => x.Value.PaymentStatus, "Cancelled"), cancellationToken: cancellation);
                if (changed.ModifiedCount != 1) throw new MongoConflictException();
                foreach (var item in current.Items)
                    await db.Products.UpdateOneAsync(session, x => x.Id == item.ProductId,
                        Builders<MongoEntity<Product>>.Update.Inc(x => x.Value.Stock, item.Quantity), cancellationToken: cancellation);
                current.PaymentStatus = "Cancelled";
                return current;
            }, ct);
            return Ok(ToStatus(cancelled));
        }
        catch (MongoConflictException) { return Conflict(new { message = "The order changed. Refresh its payment status; if you paid, contact the store." }); }
    }

    public static async Task<bool> Capture(MongoStore db, Order order, string paymentId, CancellationToken ct)
    {
        var updated = await db.Orders.UpdateOneAsync(x => x.Id == order.Id && x.Value.PaymentMethod == "Razorpay" &&
            x.Value.RazorpayOrderId == order.RazorpayOrderId && x.Value.PaymentStatus == "Pending" && x.Value.RazorpayPaymentId == null,
            Builders<MongoEntity<Order>>.Update.Set(x => x.Value.PaymentStatus, "Paid")
                .Set(x => x.Value.RazorpayPaymentId, paymentId).Set(x => x.Value.PaymentVerifiedAt, DateTime.UtcNow)
                .Set(x => x.Value.PaymentVerifiedBy, "Razorpay (captured)"), cancellationToken: ct);
        return updated.ModifiedCount == 1;
    }

    public static bool HasToken(Order order, string? token) =>
        !string.IsNullOrWhiteSpace(order.PaymentAccessTokenHash) && !string.IsNullOrWhiteSpace(token) &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(OrderPaymentSecurity.HashAccessToken(token)),
            Convert.FromHexString(order.PaymentAccessTokenHash));

    private static CustomerOrderStatusResponse ToStatus(Order order) => new(order.Id, order.Total, order.CreatedAt,
        order.PaymentStatus, order.PaymentReference, order.PaymentReportedAt, order.Items, order.PaymentMethod,
        order.FulfillmentStatus, order.TrackingId, order.MobileNumber);
}