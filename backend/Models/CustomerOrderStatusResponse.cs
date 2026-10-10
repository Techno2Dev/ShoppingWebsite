namespace backend.Models;

public sealed record CustomerOrderStatusResponse(
    int Id,
    decimal Total,
    DateTime CreatedAt,
    string PaymentStatus,
    string? PaymentReference,
    DateTime? PaymentReportedAt,
    List<OrderItem> Items);
