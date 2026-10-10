using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace backend.Data;

public sealed class AdminBearerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    StoreDbContext db) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = authorization[7..].Trim();
        if (!AdminSessionTokens.IsValidFormat(token))
        {
            return AuthenticateResult.Fail("Invalid admin session.");
        }

        var tokenHash = AdminSessionTokens.Hash(token);
        var session = await db.AdminSessions.AsNoTracking().Include(item => item.AdminUser)
            .SingleOrDefaultAsync(item => item.TokenHash == tokenHash && item.ExpiresAt > DateTime.UtcNow,
                Context.RequestAborted);
        if (session is null)
        {
            return AuthenticateResult.Fail("Admin session expired or revoked.");
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, session.AdminUserId.ToString()),
            new Claim(ClaimTypes.Name, session.AdminUser.UserName),
            new Claim(ClaimTypes.Role, "Admin")
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
