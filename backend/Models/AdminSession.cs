namespace backend.Models;

public sealed class AdminSession
{
    public required string TokenHash { get; set; }
    public int AdminUserId { get; set; }
    public required AdminUser AdminUser { get; set; }
    public DateTime ExpiresAt { get; set; }
}
