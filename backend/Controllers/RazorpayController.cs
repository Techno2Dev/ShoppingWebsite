using System.Security.Cryptography;
using System.Text.Json;
using backend.Data;
using backend.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace backend.Controllers;

[ApiController]
[Route("api/razorpay")]
public sealed class RazorpayController(StoreDbContext db, RazorpayTestGateway gateway, ILogger<RazorpayController> logger) : ControllerBase
{
    [HttpGet("availability")]
    public IActionResult Availability() => Ok(new { enabled = gateway.Enabled });

    [HttpPost("orders/{id:int}/checkout")]
    public async Task<IActionResult> Checkout(int id, ReportUpiPaymentRequest request, CancellationToken cancellationToken)
    {
        if (!gateway.Enabled) return Conflict(new { message = "Razorpay test mode is not configured." });
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Serialize checkout starts against cancellations and other starts for the same order.
        var order = await db.Orders.FromSqlInterpolated($"SELECT * FROM \"Orders\" WHERE \"Id\" = {id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (order is null || !HasToken(order, request.AccessToken)) return NotFound();
        if (order.PaymentMethod != "Razorpay" || order.PaymentStatus != "Pending")
            return Conflict(new { message = "This order cannot be paid with Razorpay." });
        if (order.RazorpayOrderId is null)
        {
            try
            {
                order.RazorpayOrderId = await gateway.CreateOrderAsync(order.Id, order.Total, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                logger.LogWarning("Razorpay test order creation failed for store order {OrderId}.", order.Id);
            }
            if (order.RazorpayOrderId is null)
                return StatusCode(502, new { message = "Could not start Razorpay checkout. Try again later." });
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return Ok(new { keyId = gateway.KeyId, orderId = order.RazorpayOrderId, amount = RazorpayTestGateway.Paise(order.Total), currency = "INR" });
    }

    [HttpPost("orders/{id:int}/verify")]
    public async Task<IActionResult> Verify(int id, VerifyRazorpayPaymentRequest request, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (order is null || !HasToken(order, request.AccessToken)) return NotFound();
        if (order.PaymentMethod != "Razorpay" || order.RazorpayOrderId is null ||
            !gateway.VerifyCheckout(order.RazorpayOrderId, request.PaymentId, request.Signature))
            return BadRequest(new { message = "Payment verification failed." });
        return await ConfirmCapturedAsync(order, request.PaymentId, cancellationToken);
    }

    [HttpPost("webhook")]
    public async Task<IActionResult> Webhook(CancellationToken cancellationToken)
    {
        if (!gateway.WebhookEnabled) return NotFound();
        using var reader = new StreamReader(Request.Body);
        var raw = await reader.ReadToEndAsync(cancellationToken);
        if (raw.Length > 100_000 || !gateway.VerifyWebhook(raw, Request.Headers["X-Razorpay-Signature"].ToString()))
            return Unauthorized();
        using var body = JsonDocument.Parse(raw);
        if (body.RootElement.GetProperty("event").GetString() != "payment.captured") return Ok();
        var payment = body.RootElement.GetProperty("payload").GetProperty("payment").GetProperty("entity");
        var paymentId = payment.GetProperty("id").GetString();
        var razorpayOrderId = payment.GetProperty("order_id").GetString();
        if (paymentId is null || razorpayOrderId is null) return BadRequest();
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(item => item.RazorpayOrderId == razorpayOrderId, cancellationToken);
        if (order is null) return Ok();
        var result = await ConfirmCapturedAsync(order, paymentId, cancellationToken);
        return result is ObjectResult objectResult && objectResult.StatusCode == 503 ? result : Ok();
    }

    private async Task<IActionResult> ConfirmCapturedAsync(Order order, string paymentId, CancellationToken cancellationToken)
    {
        bool captured;
        try { captured = await gateway.IsCapturedAsync(paymentId, order.RazorpayOrderId!, order.Total, cancellationToken); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("Could not confirm capture for store order {OrderId}.", order.Id);
            return StatusCode(503, new { message = "Payment verification is temporarily unavailable; check status shortly." });
        }
        if (!captured) return Conflict(new { message = "Payment is not captured yet. Check status shortly." });
        if (order.PaymentStatus == "Paid" && order.RazorpayPaymentId == paymentId) return Ok(new { paymentStatus = "Paid" });
        var updated = await db.Orders.Where(item => item.Id == order.Id && item.PaymentMethod == "Razorpay" &&
                item.RazorpayOrderId == order.RazorpayOrderId && item.PaymentStatus == "Pending" && item.RazorpayPaymentId == null)
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.PaymentStatus, "Paid")
                .SetProperty(item => item.RazorpayPaymentId, paymentId)
                .SetProperty(item => item.PaymentVerifiedAt, (DateTime?)DateTime.UtcNow)
                .SetProperty(item => item.PaymentVerifiedBy, "Razorpay (captured)"), cancellationToken);
        return updated == 1 ? Ok(new { paymentStatus = "Paid" }) : Conflict(new { message = "Order status changed; check with the store." });
    }

    private static bool HasToken(Order order, string token) =>
        !string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(order.PaymentAccessTokenHash) &&
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(OrderPaymentSecurity.HashAccessToken(token)),
            Convert.FromHexString(order.PaymentAccessTokenHash));
}

public sealed class VerifyRazorpayPaymentRequest
{
    public required string AccessToken { get; set; }
    public required string PaymentId { get; set; }
    public required string Signature { get; set; }
}
