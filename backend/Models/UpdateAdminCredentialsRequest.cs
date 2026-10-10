namespace backend.Models;

public sealed class UpdateAdminCredentialsRequest
{
    public required string CurrentPassword { get; set; }
    public required string NewUserName { get; set; }
    public required string NewPassword { get; set; }
}
