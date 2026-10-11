using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using backend.Mongo;

namespace backend.Data;

public sealed class AdminBearerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IServiceProvider services) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
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
        var mongo = services.GetService<MongoStore>();
        var session = mongo is null ? await services.GetRequiredService<StoreDbContext>().AdminSessions.AsNoTracking()
            .Include(item => item.AdminUser).SingleOrDefaultAsync(item => item.TokenHash == tokenHash && item.ExpiresAt > DateTime.UtcNow,
                Context.RequestAborted) : null;
        var mongoUser = mongo is null ? null : await mongo.AuthenticateAdminAsync(tokenHash, Context.RequestAborted);
        if (session is null && mongoUser is null)
        {
            return AuthenticateResult.Fail("Admin session expired or revoked.");
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, (mongoUser?.Id ?? session!.AdminUserId).ToString()),
            new Claim(ClaimTypes.Name, mongoUser?.UserName ?? session!.AdminUser.UserName),
            new Claim(ClaimTypes.Role, "Admin")
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
