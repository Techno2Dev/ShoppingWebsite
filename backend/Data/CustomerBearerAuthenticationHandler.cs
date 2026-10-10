using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace backend.Data;

public sealed class CustomerBearerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    StoreDbContext db) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var token = header[7..].Trim();
        if (!AdminSessionTokens.IsValidFormat(token)) return AuthenticateResult.Fail("Invalid session.");
        var hash = AdminSessionTokens.Hash(token);
        var session = await db.CustomerSessions.AsNoTracking().Include(item => item.CustomerUser)
            .SingleOrDefaultAsync(item => item.TokenHash == hash && item.ExpiresAt > DateTime.UtcNow, Context.RequestAborted);
        if (session is null) return AuthenticateResult.Fail("Session expired or revoked.");
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, session.CustomerUserId.ToString()),
            new Claim(ClaimTypes.Name, session.CustomerUser.Name),
            new Claim(ClaimTypes.Role, "Customer")
        }, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
