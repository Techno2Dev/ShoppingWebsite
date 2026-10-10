namespace backend.Models;

public sealed class CreateOrderRequest
{
    public required string CustomerName { get; set; }
    public required string Email { get; set; }
    public string? MobileNumber { get; set; }
    public required string Address { get; set; }
    public required List<CreateOrderItemRequest> Items { get; set; }
    public string PaymentMethod { get; set; } = "UPI";
}

public sealed class CreateOrderItemRequest
{
    public int ProductId { get; set; }
    public int Quantity { get; set; }
}
