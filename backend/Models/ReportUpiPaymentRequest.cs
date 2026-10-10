namespace backend.Models;

public sealed class ReportUpiPaymentRequest
{
    public required string AccessToken { get; set; }
    public string? TransactionReference { get; set; }
}
