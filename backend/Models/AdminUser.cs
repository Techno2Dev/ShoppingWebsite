namespace backend.Models;

public sealed class AdminUser
{
    public int Id { get; set; }
    public required string UserName { get; set; }
    public required string NormalizedUserName { get; set; }
    public required string PasswordHash { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
