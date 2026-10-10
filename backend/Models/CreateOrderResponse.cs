namespace backend.Models;

public sealed record CreateOrderResponse(
    int Id,
    decimal Total,
    DateTime CreatedAt,
    string PaymentStatus,
    List<OrderItem> Items,
    string AccessToken,
    string PaymentMethod);
