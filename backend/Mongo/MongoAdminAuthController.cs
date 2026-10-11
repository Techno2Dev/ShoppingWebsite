using System.Security.Claims;
using backend.Data;
using backend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;

namespace backend.Mongo;

[ApiController]
[Route("api/admin/auth")]
public sealed class MongoAdminAuthController(MongoStore db) : ControllerBase
{
    [HttpPost("login")]
    public async Task<IActionResult> Login(AdminLoginRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrEmpty(request.Password))
            return Unauthorized(new { message = "Username or password is incorrect." });
        var token = AdminSessionTokens.Create();
        AdminUser user;
        try
        {
            user = await db.Transact(async (session, cancellation) =>
            {
                var current = (await db.AdminUsers.Find(session,
                    x => x.Value.NormalizedUserName == request.UserName.Trim().ToUpperInvariant()).FirstOrDefaultAsync(cancellation))?.Value;
                if (current is null || !PasswordHashing.Verify(request.Password, current.PasswordHash))
                    throw new MongoConflictException();
                var touched = await db.AdminUsers.UpdateOneAsync(session, x => x.Id == current.Id && x.Value.PasswordHash == current.PasswordHash,
                    Builders<MongoEntity<AdminUser>>.Update.Inc(x => x.LoginSequence, 1), cancellationToken: cancellation);
                if (touched.ModifiedCount != 1) throw new MongoConflictException();
                await db.AdminSessions.DeleteManyAsync(session, Builders<MongoSession>.Filter.Lte(x => x.ExpiresAt, DateTime.UtcNow),
                    cancellationToken: cancellation);
                await db.AdminSessions.InsertOneAsync(session, new MongoSession { TokenHash = AdminSessionTokens.Hash(token), UserId = current.Id,
                    ExpiresAt = DateTime.UtcNow.AddHours(8) }, cancellationToken: cancellation);
                return current;
            }, ct);
        }
        catch (MongoConflictException) { return Unauthorized(new { message = "Username or password is incorrect." }); }
        Response.Headers.CacheControl = "no-store";
        return Ok(new { userName = user.UserName, accessToken = token });
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpGet("session")]
    public IActionResult Session() => Ok(new { userName = User.Identity?.Name });

    [Authorize(Policy = "AdminOnly")]
    [HttpPost("credentials")]
    public async Task<IActionResult> UpdateCredentials(UpdateAdminCredentialsRequest request, CancellationToken ct)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized();
        var userName = request.NewUserName.Trim();
        if (userName.Length is < 3 or > 100 || userName.Any(char.IsWhiteSpace) ||
            string.IsNullOrWhiteSpace(request.CurrentPassword) || request.NewPassword.Length is < 12 or > 200)
            return BadRequest(new { message = "Use a 3–100 character username without spaces and a new password of at least 12 characters." });
        var user = (await db.AdminUsers.Find(x => x.Id == userId).FirstOrDefaultAsync(ct))?.Value;
        if (user is null || !PasswordHashing.Verify(request.CurrentPassword, user.PasswordHash))
            return BadRequest(new { message = "The current password is incorrect." });
        var normalized = userName.ToUpperInvariant();
        if (await db.AdminUsers.Find(x => x.Id != userId && x.Value.NormalizedUserName == normalized).AnyAsync(ct))
            return Conflict(new { message = "That username is already in use." });
        var token = AdminSessionTokens.Create();
        try
        {
            await db.Transact(async (session, cancellation) =>
            {
                var result = await db.AdminUsers.UpdateOneAsync(session,
                    x => x.Id == userId && x.Value.PasswordHash == user.PasswordHash,
                    Builders<MongoEntity<AdminUser>>.Update.Set(x => x.Value.UserName, userName)
                        .Set(x => x.Value.NormalizedUserName, normalized)
                        .Set(x => x.Value.PasswordHash, PasswordHashing.Hash(request.NewPassword)), cancellationToken: cancellation);
                if (result.ModifiedCount != 1) throw new MongoConflictException();
                await db.AdminSessions.DeleteManyAsync(session, Builders<MongoSession>.Filter.Where(x => x.UserId == userId), cancellationToken: cancellation);
                await db.AdminSessions.InsertOneAsync(session, new MongoSession { TokenHash = AdminSessionTokens.Hash(token), UserId = userId,
                    ExpiresAt = DateTime.UtcNow.AddHours(8) }, cancellationToken: cancellation);
                return true;
            }, ct);
        }
        catch (MongoConflictException) { return Conflict(new { message = "Credentials changed. Sign in again." }); }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        { return Conflict(new { message = "That username is already in use." }); }
        Response.Headers.CacheControl = "no-store";
        return Ok(new { userName, accessToken = token });
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var token = Request.Headers.Authorization.ToString()[7..].Trim();
        await db.AdminSessions.DeleteOneAsync(x => x.TokenHash == AdminSessionTokens.Hash(token), ct);
        return NoContent();
    }
}