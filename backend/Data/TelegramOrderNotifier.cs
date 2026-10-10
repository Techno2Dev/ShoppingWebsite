using System.Globalization;
using System.Text.RegularExpressions;

namespace backend.Data;

/// <summary>Best-effort Telegram alerts; notification failures never change order/payment state.</summary>
public sealed class TelegramOrderNotifier(IConfiguration configuration, ILogger<TelegramOrderNotifier> logger) : IDisposable
{
    // A private client avoids logging the request URI, which contains the Telegram bot token.
    private readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly string botToken = configuration["Telegram:BotToken"]?.Trim() ?? "";
    private readonly string chatId = configuration["Telegram:ChatId"]?.Trim() ?? "";

    public Task OrderCreatedAsync(int orderId, decimal total) => SendAsync(
        $"New order #{orderId} received (INR {total.ToString("0.00", CultureInfo.InvariantCulture)}).\n" +
        "Payment: Pending. Wait for the customer to submit a UTR, then check your bank/UPI statement before marking Paid.");

    public Task PaymentReportedAsync(int orderId) => SendAsync(
        $"UPI payment reported for order #{orderId}.\n" +
        "Payment is still Pending. Check the submitted UTR, amount and actual credit in your bank/UPI statement before marking Paid.");

    private async Task SendAsync(string message)
    {
        if (string.IsNullOrEmpty(botToken) && string.IsNullOrEmpty(chatId))
        {
            return; // Optional integration; no Telegram request when not configured.
        }

        if (!Regex.IsMatch(botToken, @"^\d+:[A-Za-z0-9_-]+$") ||
            !Regex.IsMatch(chatId, @"^(?:-?\d+|@[A-Za-z0-9_]{5,32})$"))
        {
            logger.LogWarning("Telegram notification is not configured correctly; check Telegram__BotToken and Telegram__ChatId.");
            return;
        }

        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["chat_id"] = chatId,
                ["text"] = message
            });
            using var response = await client.PostAsync(
                $"https://api.telegram.org/bot{botToken}/sendMessage", content);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Telegram notification could not be delivered (HTTP {StatusCode}).", (int)response.StatusCode);
            }
        }
        catch (HttpRequestException)
        {
            // Do not log the exception: .NET may include the bot token from the URL.
            logger.LogWarning("Telegram notification could not be delivered (network error).");
        }
        catch (TaskCanceledException)
        {
            logger.LogWarning("Telegram notification could not be delivered (timeout).");
        }
    }

    public void Dispose() => client.Dispose();
}
