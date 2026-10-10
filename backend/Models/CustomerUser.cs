namespace backend.Models;

public sealed class CustomerUser
{
    public int Id { get; set; }
    public required string GoogleSubject { get; set; }
    public required string Email { get; set; }
    public string? MobileNumber { get; set; }
    public required string Name { get; set; }
    public string? PhotoUrl { get; set; }
    public string? DeliveryAddress { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
