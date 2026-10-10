namespace backend.Models;

public sealed class AdminLoginRequest
{
    public required string UserName { get; set; }
    public required string Password { get; set; }
}
