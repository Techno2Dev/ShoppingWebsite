using backend.Data;
using backend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Security.Cryptography;

namespace backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class OrdersController(StoreDbContext db, TelegramOrderNotifier telegram, RazorpayTestGateway razorpay) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> GetAll([FromQuery] int page, CancellationToken cancellationToken)
    {
        if (page < 1 || page > 1000000)
        {
            return BadRequest(new { message = "Page must be between 1 and 1000000." });
        }

        const int pageSize = 15;
        var totalCount = await db.Orders.CountAsync(cancellationToken);
        var orders = await db.Orders.AsNoTracking().Include(order => order.Items)
            .OrderByDescending(order => order.CreatedAt).ThenByDescending(order => order.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return Ok(new { items = orders, totalCount, page, pageSize });
    }

    [HttpGet("{id:int}")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<Order>> GetById(int id, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking().Include(item => item.Items)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        return order is null ? NotFound() : Ok(order);
    }

    [HttpGet("mine")]
    [Authorize(Policy = "CustomerOnly")]
    public async Task<IActionResult> Mine([FromQuery] int page = 1, CancellationToken cancellationToken = default)
    {
        if (page < 1 || page > 1000000) return BadRequest(new { message = "Invalid page." });
        const int pageSize = 20;
        var customerId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var totalCount = await db.Orders.CountAsync(item => item.CustomerUserId == customerId, cancellationToken);
        var orders = await db.Orders.AsNoTracking().Where(item => item.CustomerUserId == customerId)
            .Include(item => item.Items).OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        Response.Headers.CacheControl = "no-store";
        return Ok(new { items = orders.Select(order => new
        {
            order.Id, order.CustomerName, order.Email, order.MobileNumber, order.Address, order.CreatedAt,
            order.Total, order.PaymentMethod, order.PaymentStatus,
            order.FulfillmentStatus, order.TrackingId, order.Items
        }), totalCount, page, pageSize });
    }

    [HttpPost("{id:int}/claim")]
    [Authorize(Policy = "CustomerOnly")]
    public async Task<IActionResult> Claim(int id, ReportUpiPaymentRequest request, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (order is null || !HasValidAccessToken(order, request.AccessToken)) return NotFound();
        var customerId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (order.CustomerUserId == customerId) return Ok();
        if (order.CustomerUserId is not null) return Conflict(new { message = "This order is already linked to an account." });
        var updated = await db.Orders.Where(item => item.Id == id && item.CustomerUserId == null)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.CustomerUserId, (int?)customerId), cancellationToken);
        return updated == 1 ? Ok() : Conflict(new { message = "The order was linked to another account." });
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
        if (!IndianMobileNumber.TryNormalize(request.MobileNumber, out var mobileNumber))
            return BadRequest(new { message = "Enter a valid 10-digit Indian mobile number." });
        if (request.PaymentMethod is not ("UPI" or "Razorpay"))
            return BadRequest(new { message = "Choose UPI or Razorpay." });
        if (request.PaymentMethod == "Razorpay" && !razorpay.Enabled)
            return BadRequest(new { message = "Razorpay test payments are not configured. Choose UPI instead." });

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

        var accessToken = OrderPaymentSecurity.CreateAccessToken();
        int? customerUserId = null;
        if (Request.Headers.Authorization.Count > 0)
        {
            var result = await HttpContext.AuthenticateAsync("CustomerBearer");
            if (!result.Succeeded) return Unauthorized(new { message = "Customer session expired. Sign in again or continue as a guest." });
            customerUserId = int.Parse(result.Principal!.FindFirstValue(ClaimTypes.NameIdentifier)!);
        }
        var order = new Order
        {
            CustomerName = request.CustomerName.Trim(),
            Email = request.Email.Trim(),
            MobileNumber = mobileNumber,
            Address = request.Address.Trim(),
            PaymentStatus = "Pending",
            PaymentMethod = request.PaymentMethod,
            CustomerUserId = customerUserId,
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
        if (order.PaymentMethod == "Razorpay" && order.Total < 1m)
            return BadRequest(new { message = "Razorpay requires an order of at least ₹1." });

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            foreach (var item in requestedItems)
            {
                products[item.ProductId].Stock -= item.Quantity;
            }

            db.Orders.Add(order);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await telegram.OrderCreatedAsync(order.Id, order.Total);

        var response = new CreateOrderResponse(
            order.Id,
            order.Total,
            order.CreatedAt,
            order.PaymentStatus,
            order.Items,
            accessToken,
            order.PaymentMethod,
            order.MobileNumber);
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

        if (order.PaymentMethod == "Razorpay" && order.PaymentStatus == "Pending" && order.RazorpayOrderId is not null)
        {
            try
            {
                var capturedId = await razorpay.FindCapturedPaymentAsync(order.RazorpayOrderId, order.Total, cancellationToken);
                if (capturedId is not null)
                {
                    await db.Orders.Where(item => item.Id == id && item.PaymentStatus == "Pending" &&
                            item.PaymentMethod == "Razorpay" && item.RazorpayOrderId == order.RazorpayOrderId && item.RazorpayPaymentId == null)
                        .ExecuteUpdateAsync(update => update.SetProperty(item => item.PaymentStatus, "Paid")
                            .SetProperty(item => item.RazorpayPaymentId, capturedId)
                            .SetProperty(item => item.PaymentVerifiedAt, (DateTime?)DateTime.UtcNow)
                            .SetProperty(item => item.PaymentVerifiedBy, "Razorpay (captured)"), cancellationToken);
                    await db.Entry(order).ReloadAsync(cancellationToken);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                // Keep payment Pending until Razorpay can be reached again; do not block order status access.
            }
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

        if (order.PaymentMethod != "UPI" || order.PaymentStatus != "Pending")
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
            .Where(candidate => candidate.Id == id && candidate.PaymentMethod == "UPI" && candidate.PaymentStatus == "Pending")
            .ExecuteUpdateAsync(update => update
                .SetProperty(candidate => candidate.PaymentReference, paymentReference)
                .SetProperty(candidate => candidate.PaymentReportedAt, (DateTime?)reportedAt), cancellationToken);
        if (updatedRows == 0)
        {
            return Conflict(new { message = "An admin has already changed this order's payment status." });
        }

        await db.Entry(order).ReloadAsync(cancellationToken);
        await telegram.PaymentReportedAsync(order.Id);
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

        if (order.PaymentStatus != "Pending" || order.RazorpayOrderId is not null || order.PaymentReportedAt is not null ||
            !string.IsNullOrWhiteSpace(order.PaymentReference))
        {
            return Conflict(new { message = "This order cannot be cancelled online. If you have already paid, contact the store." });
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var updatedRows = await db.Orders.Where(candidate => candidate.Id == id &&
                candidate.PaymentStatus == "Pending" && candidate.RazorpayOrderId == null && candidate.PaymentReportedAt == null && candidate.PaymentReference == null)
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
        order.Items,
        order.PaymentMethod,
        order.FulfillmentStatus,
        order.TrackingId,
        order.MobileNumber);
}
