using System.Security.Claims;
using backend.Data;
using backend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace backend.Controllers;

[ApiController]
[Route("api/admin/auth")]
public sealed class AdminAuthController(StoreDbContext db) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login(AdminLoginRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrEmpty(request.Password))
        {
            return Unauthorized(new { message = "Username or password is incorrect." });
        }

        var normalizedUserName = request.UserName.Trim().ToUpperInvariant();
        var user = await db.AdminUsers
            .SingleOrDefaultAsync(admin => admin.NormalizedUserName == normalizedUserName, cancellationToken);
        if (user is null || !PasswordHashing.Verify(request.Password, user.PasswordHash))
        {
            return Unauthorized(new { message = "Username or password is incorrect." });
        }

        await db.AdminSessions.Where(session => session.ExpiresAt <= DateTime.UtcNow)
            .ExecuteDeleteAsync(cancellationToken);
        var accessToken = AdminSessionTokens.Create();
        db.AdminSessions.Add(new AdminSession
        {
            TokenHash = AdminSessionTokens.Hash(accessToken),
            AdminUserId = user.Id,
            AdminUser = user,
            ExpiresAt = DateTime.UtcNow.AddHours(8)
        });
        await db.SaveChangesAsync(cancellationToken);
        Response.Headers.CacheControl = "no-store";
        return Ok(new { userName = user.UserName, accessToken });
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpGet("session")]
    public IActionResult Session() => Ok(new { userName = User.Identity?.Name });

    [Authorize(Policy = "AdminOnly")]
    [HttpPost("credentials")]
    public async Task<IActionResult> UpdateCredentials(UpdateAdminCredentialsRequest request, CancellationToken cancellationToken)
    {
        var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(userIdValue, out var userId))
        {
            return Unauthorized();
        }

        var userName = request.NewUserName.Trim();
        if (userName.Length is < 3 or > 100 || userName.Any(char.IsWhiteSpace) ||
            string.IsNullOrWhiteSpace(request.CurrentPassword) || request.NewPassword.Length is < 12 or > 200)
        {
            return BadRequest(new { message = "Use a 3–100 character username without spaces and a new password of at least 12 characters." });
        }

        var user = await db.AdminUsers.SingleOrDefaultAsync(admin => admin.Id == userId, cancellationToken);
        if (user is null || !PasswordHashing.Verify(request.CurrentPassword, user.PasswordHash))
        {
            return BadRequest(new { message = "The current password is incorrect." });
        }

        var normalizedUserName = userName.ToUpperInvariant();
        if (await db.AdminUsers.AnyAsync(admin => admin.Id != userId && admin.NormalizedUserName == normalizedUserName, cancellationToken))
        {
            return Conflict(new { message = "That username is already in use." });
        }

        user.UserName = userName;
        user.NormalizedUserName = normalizedUserName;
        user.PasswordHash = PasswordHashing.Hash(request.NewPassword);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.AdminSessions.Where(session => session.AdminUserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
        var accessToken = AdminSessionTokens.Create();
        db.AdminSessions.Add(new AdminSession
        {
            TokenHash = AdminSessionTokens.Hash(accessToken),
            AdminUserId = user.Id,
            AdminUser = user,
            ExpiresAt = DateTime.UtcNow.AddHours(8)
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        Response.Headers.CacheControl = "no-store";
        return Ok(new { userName = user.UserName, accessToken });
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        var token = Request.Headers.Authorization.ToString()[7..].Trim();
        await db.AdminSessions.Where(session => session.TokenHash == AdminSessionTokens.Hash(token))
            .ExecuteDeleteAsync(cancellationToken);
        return NoContent();
    }
}
