using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace backend.Data;

/// <summary>Test keys only. API secret never goes to the browser.</summary>
public sealed class RazorpayTestGateway(HttpClient http, IConfiguration config)
{
    private readonly string keyId = config["Razorpay:KeyId"]?.Trim() ?? "";
    private readonly string keySecret = config["Razorpay:KeySecret"]?.Trim() ?? "";
    private readonly string webhookSecret = config["Razorpay:WebhookSecret"]?.Trim() ?? "";
    public bool Enabled => keyId.StartsWith("rzp_test_", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(keySecret);
    public bool WebhookEnabled => Enabled && !string.IsNullOrEmpty(webhookSecret);
    public string KeyId => keyId;

    public static long Paise(decimal total) => checked((long)(total * 100m));

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, "https://api.razorpay.com/v1/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(keyId + ":" + keySecret)));
        return request;
    }

    public async Task<string?> CreateOrderAsync(int storeOrderId, decimal amount, CancellationToken cancellationToken)
    {
        if (!Enabled) return null;
        using var request = Request(HttpMethod.Post, "orders");
        var receipt = "shop_" + storeOrderId + "_" + Guid.NewGuid().ToString("N")[..16];
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            amount = Paise(amount), currency = "INR",
            receipt
        }), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return body.RootElement.GetProperty("id").GetString();
    }

    public bool VerifyCheckout(string orderId, string paymentId, string signature)
    {
        if (!Enabled || !Regex.IsMatch(orderId, @"^order_[A-Za-z0-9]+$") ||
            !Regex.IsMatch(paymentId, @"^pay_[A-Za-z0-9]+$") || !Regex.IsMatch(signature, "^[a-fA-F0-9]{64}$")) return false;
        return MatchesHmac(orderId + "|" + paymentId, signature, keySecret);
    }

    public bool VerifyWebhook(string rawBody, string signature) =>
        WebhookEnabled && Regex.IsMatch(signature, "^[a-fA-F0-9]{64}$") && MatchesHmac(rawBody, signature, webhookSecret);

    private static bool MatchesHmac(string input, string signature, string secret)
    {
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(input));
        return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(signature));
    }

    public async Task<bool> IsCapturedAsync(string paymentId, string orderId, decimal total, CancellationToken cancellationToken)
    {
        if (!Enabled || !Regex.IsMatch(paymentId, @"^pay_[A-Za-z0-9]+$")) return false;
        using var request = Request(HttpMethod.Get, "payments/" + paymentId);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return false;
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var data = body.RootElement;
        return data.GetProperty("id").GetString() == paymentId &&
               data.GetProperty("order_id").GetString() == orderId &&
               data.GetProperty("status").GetString() == "captured" &&
               data.GetProperty("currency").GetString() == "INR" &&
               data.GetProperty("amount").GetInt64() == Paise(total);
    }

    public async Task<string?> FindCapturedPaymentAsync(string orderId, decimal total, CancellationToken cancellationToken)
    {
        if (!Enabled || !Regex.IsMatch(orderId, @"^order_[A-Za-z0-9]+$")) return null;
        using var request = Request(HttpMethod.Get, "orders/" + orderId + "/payments");
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        foreach (var item in body.RootElement.GetProperty("items").EnumerateArray())
        {
            if (item.GetProperty("status").GetString() == "captured" &&
                item.GetProperty("order_id").GetString() == orderId &&
                item.GetProperty("currency").GetString() == "INR" &&
                item.GetProperty("amount").GetInt64() == Paise(total))
                return item.GetProperty("id").GetString();
        }
        return null;
    }
}
