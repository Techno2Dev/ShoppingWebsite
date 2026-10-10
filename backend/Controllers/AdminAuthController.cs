using System.Security.Claims;
using backend.Data;
using backend.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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
        var user = await db.AdminUsers.AsNoTracking()
            .SingleOrDefaultAsync(admin => admin.NormalizedUserName == normalizedUserName, cancellationToken);
        if (user is null || !PasswordHashing.Verify(request.Password, user.PasswordHash))
        {
            return Unauthorized(new { message = "Username or password is incorrect." });
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName),
            new(ClaimTypes.Role, "Admin")
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity));

        return Ok(new { userName = user.UserName });
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
        await db.SaveChangesAsync(cancellationToken);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName),
            new(ClaimTypes.Role, "Admin")
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity));

        return Ok(new { userName = user.UserName });
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }
}
