namespace backend.Models;

public sealed class CreateOrderRequest
{
    public required string CustomerName { get; set; }
    public required string Email { get; set; }
    public required string Address { get; set; }
    public required List<CreateOrderItemRequest> Items { get; set; }
}

public sealed class CreateOrderItemRequest
{
    public int ProductId { get; set; }
    public int Quantity { get; set; }
}
