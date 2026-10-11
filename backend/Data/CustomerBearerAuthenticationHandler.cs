using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using backend.Mongo;

namespace backend.Data;

public sealed class CustomerBearerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IServiceProvider services) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var token = header[7..].Trim();
        if (!AdminSessionTokens.IsValidFormat(token)) return AuthenticateResult.Fail("Invalid session.");
        var hash = AdminSessionTokens.Hash(token);
        var mongo = services.GetService<MongoStore>();
        var session = mongo is null ? await services.GetRequiredService<StoreDbContext>().CustomerSessions.AsNoTracking()
            .Include(item => item.CustomerUser).SingleOrDefaultAsync(item => item.TokenHash == hash && item.ExpiresAt > DateTime.UtcNow, Context.RequestAborted) : null;
        var mongoUser = mongo is null ? null : await mongo.AuthenticateCustomerAsync(hash, Context.RequestAborted);
        if (session is null && mongoUser is null) return AuthenticateResult.Fail("Session expired or revoked.");
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, (mongoUser?.Id ?? session!.CustomerUserId).ToString()),
            new Claim(ClaimTypes.Name, mongoUser?.Name ?? session!.CustomerUser.Name),
            new Claim(ClaimTypes.Role, "Customer")
        }, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
