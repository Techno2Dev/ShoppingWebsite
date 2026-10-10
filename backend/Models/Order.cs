namespace backend.Models;

public sealed class Order
{
    public int Id { get; set; }
    public required string CustomerName { get; set; }
    public required string Email { get; set; }
    public required string Address { get; set; }
    public string PaymentStatus { get; set; } = "Pending";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public decimal Total { get; set; }
    public string? PaymentReference { get; set; }
    public DateTime? PaymentReportedAt { get; set; }
    public DateTime? PaymentVerifiedAt { get; set; }
    public string? PaymentVerifiedBy { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? PaymentAccessTokenHash { get; set; }
    public List<OrderItem> Items { get; set; } = [];
}
