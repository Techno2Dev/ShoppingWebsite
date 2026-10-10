using System.Security.Claims;
using backend.Data;
using backend.Models;
using Google.Apis.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace backend.Controllers;

[ApiController]
[Route("api/customer")]
public sealed class CustomerAuthController(StoreDbContext db, IConfiguration configuration) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("google/config")]
    public IActionResult GoogleConfig() => Ok(new { clientId = configuration["Google:ClientId"]?.Trim() ?? "" });

    [AllowAnonymous]
    [HttpPost("google/login")]
    public async Task<IActionResult> Login(GoogleLoginRequest request, CancellationToken cancellationToken)
    {
        var clientId = configuration["Google:ClientId"]?.Trim();
        if (string.IsNullOrWhiteSpace(clientId)) return StatusCode(503, new { message = "Google login is not configured." });
        if (string.IsNullOrWhiteSpace(request.Credential) || request.Credential.Length > 8192)
            return Unauthorized(new { message = "Invalid Google sign-in response." });

        GoogleJsonWebSignature.Payload google;
        try
        {
            google = await GoogleJsonWebSignature.ValidateAsync(request.Credential,
                new GoogleJsonWebSignature.ValidationSettings { Audience = new[] { clientId } });
        }
        catch (InvalidJwtException)
        {
            return Unauthorized(new { message = "Google sign-in could not be verified." });
        }

        if (google.EmailVerified != true || string.IsNullOrWhiteSpace(google.Subject) ||
            string.IsNullOrWhiteSpace(google.Email))
            return Unauthorized(new { message = "A verified Google email is required." });

        var customer = await db.CustomerUsers.SingleOrDefaultAsync(user => user.GoogleSubject == google.Subject, cancellationToken);
        if (customer is null)
        {
            customer = new CustomerUser
            {
                GoogleSubject = google.Subject,
                Email = google.Email[..Math.Min(google.Email.Length, 254)],
                Name = (string.IsNullOrWhiteSpace(google.Name) ? google.Email : google.Name)[..Math.Min((string.IsNullOrWhiteSpace(google.Name) ? google.Email : google.Name).Length, 120)],
                PhotoUrl = ValidPhoto(google.Picture)
            };
            db.CustomerUsers.Add(customer);
        }
        else
        {
            customer.Email = google.Email[..Math.Min(google.Email.Length, 254)];
            customer.PhotoUrl = ValidPhoto(google.Picture);
        }

        var token = AdminSessionTokens.Create();
        db.CustomerSessions.Add(new CustomerSession
        {
            TokenHash = AdminSessionTokens.Hash(token),
            CustomerUser = customer,
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        });
        await db.SaveChangesAsync(cancellationToken);
        Response.Headers.CacheControl = "no-store";
        return Ok(new { accessToken = token, customer = Profile(customer) });
    }

    [Authorize(Policy = "CustomerOnly")]
    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        var customer = await CurrentCustomer(cancellationToken);
        return customer is null ? Unauthorized() : Ok(Profile(customer));
    }

    [Authorize(Policy = "CustomerOnly")]
    [HttpPut("me")]
    public async Task<IActionResult> Update(UpdateCustomerDetailsRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 120 ||
            request.DeliveryAddress?.Length > 500)
            return BadRequest(new { message = "Enter a name (up to 120 characters) and address (up to 500 characters)." });
        string? mobileNumber = null;
        if (!string.IsNullOrWhiteSpace(request.MobileNumber))
        {
            if (!IndianMobileNumber.TryNormalize(request.MobileNumber, out var validNumber))
                return BadRequest(new { message = "Enter a valid 10-digit Indian mobile number." });
            mobileNumber = validNumber;
        }
        var customer = await CurrentCustomer(cancellationToken);
        if (customer is null) return Unauthorized();
        customer.Name = request.Name.Trim();
        customer.MobileNumber = mobileNumber;
        customer.DeliveryAddress = string.IsNullOrWhiteSpace(request.DeliveryAddress) ? null : request.DeliveryAddress.Trim();
        await db.SaveChangesAsync(cancellationToken);
        return Ok(Profile(customer));
    }

    [Authorize(Policy = "CustomerOnly")]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        var token = Request.Headers.Authorization.ToString()[7..].Trim();
        await db.CustomerSessions.Where(item => item.TokenHash == AdminSessionTokens.Hash(token))
            .ExecuteDeleteAsync(cancellationToken);
        return NoContent();
    }

    private Task<CustomerUser?> CurrentCustomer(CancellationToken cancellationToken) =>
        db.CustomerUsers.SingleOrDefaultAsync(item => item.Id == int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!), cancellationToken);

    private static object Profile(CustomerUser user) => new { user.Id, user.Name, user.Email, user.MobileNumber, user.PhotoUrl, user.DeliveryAddress };

    private static string? ValidPhoto(string? url) => url is { Length: < 1000 } &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? url : null;
}

public sealed class GoogleLoginRequest { public required string Credential { get; set; } }
public sealed class UpdateCustomerDetailsRequest
{
    public required string Name { get; set; }
    public string? MobileNumber { get; set; }
    public string? DeliveryAddress { get; set; }
}
