using System.Text.Json;
using backend.Data;
using backend.Models;
using backend.Controllers;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;

namespace backend.Mongo;

[ApiController]
[Route("api/razorpay")]
public sealed class MongoRazorpayController(MongoStore db, RazorpayTestGateway gateway, ILogger<MongoRazorpayController> logger) : ControllerBase
{
    [HttpGet("availability")]
    public IActionResult Availability() => Ok(new { enabled = gateway.Enabled });

    [HttpPost("orders/{id:int}/checkout")]
    public async Task<IActionResult> Checkout(int id, ReportUpiPaymentRequest request, CancellationToken ct)
    {
        if (!gateway.Enabled) return Conflict(new { message = "Razorpay test mode is not configured." });
        var order = (await db.Orders.Find(x => x.Id == id).FirstOrDefaultAsync(ct))?.Value;
        if (order is null || !MongoOrdersController.HasToken(order, request.AccessToken)) return NotFound();
        if (order.PaymentMethod != "Razorpay" || order.PaymentStatus != "Pending")
            return Conflict(new { message = "This order cannot be paid with Razorpay." });
        if (order.RazorpayOrderId is not null) return CheckoutDetails(order);

        // An atomic lease serializes provider calls against cancellation and other checkouts.
        // A crashed provider call may leave an orphan Razorpay order, never a charged store order.
        var lease = Guid.NewGuid().ToString("N");
        var reserved = await db.Orders.FindOneAndUpdateAsync(x => x.Id == id && x.Value.PaymentMethod == "Razorpay" &&
            x.Value.PaymentStatus == "Pending" && x.Value.RazorpayOrderId == null &&
            (x.CheckoutLeaseUntil == null || x.CheckoutLeaseUntil <= DateTime.UtcNow),
            Builders<MongoEntity<Order>>.Update.Set(x => x.CheckoutLeaseId, lease)
                .Set(x => x.CheckoutLeaseUntil, DateTime.UtcNow.AddMinutes(2)),
            new FindOneAndUpdateOptions<MongoEntity<Order>> { ReturnDocument = ReturnDocument.After }, ct);
        if (reserved is null)
        {
            var latest = (await db.Orders.Find(x => x.Id == id).FirstOrDefaultAsync(ct))?.Value;
            return latest?.RazorpayOrderId is not null && latest.PaymentStatus == "Pending"
                ? CheckoutDetails(latest) : Conflict(new { message = "Checkout is already starting or the order changed. Try again shortly." });
        }
        try
        {
            var razorpayId = await gateway.CreateOrderAsync(id, order.Total, ct);
            if (razorpayId is null) return StatusCode(502, new { message = "Could not start Razorpay checkout. Try again later." });
            var updated = await db.Orders.UpdateOneAsync(x => x.Id == id && x.CheckoutLeaseId == lease &&
                x.Value.PaymentStatus == "Pending" && x.Value.RazorpayOrderId == null,
                Builders<MongoEntity<Order>>.Update.Set(x => x.Value.RazorpayOrderId, razorpayId)
                    .Set(x => x.CheckoutLeaseId, null).Set(x => x.CheckoutLeaseUntil, null), cancellationToken: ct);
            if (updated.ModifiedCount != 1) return Conflict(new { message = "Checkout state changed. Contact the store before retrying." });
            order.RazorpayOrderId = razorpayId;
            return CheckoutDetails(order);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("Razorpay test order creation failed for store order {OrderId}.", id);
            return StatusCode(502, new { message = "Could not start Razorpay checkout. Try again later." });
        }
        finally
        {
            // Clear only our own lease. Failure to clear delays retries until its bounded expiry.
            await db.Orders.UpdateOneAsync(x => x.Id == id && x.CheckoutLeaseId == lease,
                Builders<MongoEntity<Order>>.Update.Set(x => x.CheckoutLeaseId, null).Set(x => x.CheckoutLeaseUntil, null),
                cancellationToken: CancellationToken.None);
        }
    }

    [HttpPost("orders/{id:int}/verify")]
    public async Task<IActionResult> Verify(int id, VerifyRazorpayPaymentRequest request, CancellationToken ct)
    {
        var order = (await db.Orders.Find(x => x.Id == id).FirstOrDefaultAsync(ct))?.Value;
        if (order is null || !MongoOrdersController.HasToken(order, request.AccessToken)) return NotFound();
        if (order.PaymentMethod != "Razorpay" || order.RazorpayOrderId is null ||
            !gateway.VerifyCheckout(order.RazorpayOrderId, request.PaymentId, request.Signature))
            return BadRequest(new { message = "Payment verification failed." });
        return await ConfirmCaptured(order, request.PaymentId, ct);
    }

    [HttpPost("webhook")]
    public async Task<IActionResult> Webhook(CancellationToken ct)
    {
        if (!gateway.WebhookEnabled) return NotFound();
        using var reader = new StreamReader(Request.Body);
        var raw = await reader.ReadToEndAsync(ct);
        if (raw.Length > 100_000 || !gateway.VerifyWebhook(raw, Request.Headers["X-Razorpay-Signature"].ToString()))
            return Unauthorized();
        using var body = JsonDocument.Parse(raw);
        if (body.RootElement.GetProperty("event").GetString() != "payment.captured") return Ok();
        var payment = body.RootElement.GetProperty("payload").GetProperty("payment").GetProperty("entity");
        var paymentId = payment.GetProperty("id").GetString();
        var providerId = payment.GetProperty("order_id").GetString();
        if (paymentId is null || providerId is null) return BadRequest();
        var order = (await db.Orders.Find(x => x.Value.RazorpayOrderId == providerId).FirstOrDefaultAsync(ct))?.Value;
        if (order is null) return Ok();
        var result = await ConfirmCaptured(order, paymentId, ct);
        return result is ObjectResult response && response.StatusCode == 503 ? result : Ok();
    }

    private async Task<IActionResult> ConfirmCaptured(Order order, string paymentId, CancellationToken ct)
    {
        bool captured;
        try { captured = await gateway.IsCapturedAsync(paymentId, order.RazorpayOrderId!, order.Total, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("Could not confirm capture for store order {OrderId}.", order.Id);
            return StatusCode(503, new { message = "Payment verification is temporarily unavailable; check status shortly." });
        }
        if (!captured) return Conflict(new { message = "Payment is not captured yet. Check status shortly." });
        if (order.PaymentStatus == "Paid" && order.RazorpayPaymentId == paymentId) return Ok(new { paymentStatus = "Paid" });
        var updated = await MongoOrdersController.Capture(db, order, paymentId, ct);
        return updated ? Ok(new { paymentStatus = "Paid" }) : Conflict(new { message = "Order status changed; check with the store." });
    }

    private IActionResult CheckoutDetails(Order order) => Ok(new { keyId = gateway.KeyId, orderId = order.RazorpayOrderId,
        amount = RazorpayTestGateway.Paise(order.Total), currency = "INR" });
}