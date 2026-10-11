using System.Security.Claims;
using backend.Data;
using backend.Models;
using backend.Controllers;
using Google.Apis.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;

namespace backend.Mongo;

[ApiController]
[Route("api/customer")]
public sealed class MongoCustomerAuthController(MongoStore db, IConfiguration configuration) : ControllerBase
{
    [HttpGet("google/config")]
    public IActionResult GoogleConfig() => Ok(new { clientId = configuration["Google:ClientId"]?.Trim() ?? "" });

    [HttpPost("google/login")]
    public async Task<IActionResult> Login(GoogleLoginRequest request, CancellationToken ct)
    {
        var clientId = configuration["Google:ClientId"]?.Trim();
        if (string.IsNullOrWhiteSpace(clientId)) return StatusCode(503, new { message = "Google login is not configured." });
        if (string.IsNullOrWhiteSpace(request.Credential) || request.Credential.Length > 8192)
            return Unauthorized(new { message = "Invalid Google sign-in response." });
        GoogleJsonWebSignature.Payload google;
        try { google = await GoogleJsonWebSignature.ValidateAsync(request.Credential,
            new GoogleJsonWebSignature.ValidationSettings { Audience = new[] { clientId } }); }
        catch (InvalidJwtException) { return Unauthorized(new { message = "Google sign-in could not be verified." }); }
        if (google.EmailVerified != true || string.IsNullOrWhiteSpace(google.Subject) || string.IsNullOrWhiteSpace(google.Email))
            return Unauthorized(new { message = "A verified Google email is required." });

        var name = string.IsNullOrWhiteSpace(google.Name) ? google.Email : google.Name;
        var photo = google.Picture is { Length: < 1000 } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps ? url : null;
        var accessToken = AdminSessionTokens.Create();
        async Task<CustomerUser> SignIn()
        {
            return await db.Transact(async (session, cancellation) =>
            {
                var existing = await db.CustomerUsers.Find(session, x => x.Value.GoogleSubject == google.Subject).FirstOrDefaultAsync(cancellation);
                if (existing is null)
                {
                    var id = await db.NextId("customer", session, cancellation);
                    existing = new MongoEntity<CustomerUser> { Id = id, Value = new CustomerUser
                    {
                        Id = id, GoogleSubject = google.Subject, Email = google.Email[..Math.Min(google.Email.Length, 254)],
                        Name = name[..Math.Min(name.Length, 120)], PhotoUrl = photo
                    } };
                    await db.CustomerUsers.InsertOneAsync(session, existing, cancellationToken: cancellation);
                }
                else
                {
                    await db.CustomerUsers.UpdateOneAsync(session, x => x.Id == existing.Id,
                        Builders<MongoEntity<CustomerUser>>.Update.Set(x => x.Value.Email, google.Email[..Math.Min(google.Email.Length, 254)])
                            .Set(x => x.Value.PhotoUrl, photo), cancellationToken: cancellation);
                    existing.Value.Email = google.Email[..Math.Min(google.Email.Length, 254)];
                    existing.Value.PhotoUrl = photo;
                }
                await db.CustomerSessions.InsertOneAsync(session, new MongoSession { TokenHash = AdminSessionTokens.Hash(accessToken),
                    UserId = existing.Id, ExpiresAt = DateTime.UtcNow.AddDays(7) }, cancellationToken: cancellation);
                return existing.Value;
            }, ct);
        }
        CustomerUser customer;
        try { customer = await SignIn(); }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // Another login created the Google subject concurrently; retry against that account.
            customer = await SignIn();
        }
        Response.Headers.CacheControl = "no-store";
        return Ok(new { accessToken, customer = Profile(customer) });
    }

    [Authorize(Policy = "CustomerOnly")]
    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var customer = await CurrentCustomer(ct);
        return customer is null ? Unauthorized() : Ok(Profile(customer));
    }

    [Authorize(Policy = "CustomerOnly")]
    [HttpPut("me")]
    public async Task<IActionResult> Update(UpdateCustomerDetailsRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 120 || request.DeliveryAddress?.Length > 500)
            return BadRequest(new { message = "Enter a name (up to 120 characters) and address (up to 500 characters)." });
        string? mobile = null;
        if (!string.IsNullOrWhiteSpace(request.MobileNumber))
        {
            if (!IndianMobileNumber.TryNormalize(request.MobileNumber, out var valid))
                return BadRequest(new { message = "Enter a valid 10-digit Indian mobile number." });
            mobile = valid;
        }
        var id = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var customer = await db.CustomerUsers.FindOneAndUpdateAsync(x => x.Id == id,
            Builders<MongoEntity<CustomerUser>>.Update.Set(x => x.Value.Name, request.Name.Trim())
                .Set(x => x.Value.MobileNumber, mobile)
                .Set(x => x.Value.DeliveryAddress, string.IsNullOrWhiteSpace(request.DeliveryAddress) ? null : request.DeliveryAddress.Trim()),
            new FindOneAndUpdateOptions<MongoEntity<CustomerUser>> { ReturnDocument = ReturnDocument.After }, ct);
        return customer is null ? Unauthorized() : Ok(Profile(customer.Value));
    }

    [Authorize(Policy = "CustomerOnly")]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var token = Request.Headers.Authorization.ToString()[7..].Trim();
        await db.CustomerSessions.DeleteOneAsync(x => x.TokenHash == AdminSessionTokens.Hash(token), ct);
        return NoContent();
    }

    private async Task<CustomerUser?> CurrentCustomer(CancellationToken ct) =>
        (await db.CustomerUsers.Find(x => x.Id == int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!))
            .FirstOrDefaultAsync(ct))?.Value;
    private static object Profile(CustomerUser user) => new { user.Id, user.Name, user.Email, user.MobileNumber, user.PhotoUrl, user.DeliveryAddress };
}