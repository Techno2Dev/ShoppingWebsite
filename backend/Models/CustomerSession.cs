namespace backend.Models;

public sealed class CustomerSession
{
    public required string TokenHash { get; set; }
    public int CustomerUserId { get; set; }
    public required CustomerUser CustomerUser { get; set; }
    public DateTime ExpiresAt { get; set; }
}
