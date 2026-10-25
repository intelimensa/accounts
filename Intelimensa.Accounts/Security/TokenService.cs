using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Intelimensa.Accounts.Security;

/// <summary>
/// Issues short-lived JWT access tokens and longer-lived opaque refresh tokens. Refresh tokens
/// are stored hashed (SHA-256) -- the raw value is only ever handed back to the caller once, at
/// issuance -- and rotate on every use (<see cref="FindActiveRefreshTokenAsync"/>, then
/// <see cref="RotateAsync"/>). Tokens from a login that registered a <see cref="ClientInstall"/> are bound
/// to it: the access token carries <see cref="ClientInstallClaim"/>, and the refresh token's row the install.
/// </summary>
public class TokenService(ApplicationDbContext db, IConfiguration configuration)
{
    /// <summary>The bound install's id, in an access token. Requests on such a token need that install's proof.</summary>
    public const string ClientInstallClaim = "client_install";

    public (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(
        ApplicationUser user, IEnumerable<string> roles, Guid? clientInstallId = null)
    {
        var lifetime = TimeSpan.FromMinutes(configuration.GetValue("Jwt:AccessTokenLifetimeMinutes", 15));
        var expiresAt = DateTimeOffset.UtcNow.Add(lifetime);

        // Role claims let the API authorize by role (e.g. the Manufacturer policy). They're baked
        // in at issuance, so a role change takes effect on the next refresh (<= access-token lifetime).
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        if (clientInstallId is { } installId)
            claims.Add(new Claim(ClientInstallClaim, installId.ToString("D")));

        var token = new JwtSecurityToken(
            issuer: configuration["Jwt:Issuer"],
            audience: configuration["Jwt:Audience"],
            claims: claims,
            expires: expiresAt.UtcDateTime,
            signingCredentials: new SigningCredentials(GetSigningKey(), SecurityAlgorithms.HmacSha256));

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    public async Task<(string Token, DateTimeOffset ExpiresAt)> CreateRefreshTokenAsync(
        string userId, Guid? clientInstallId = null, CancellationToken ct = default)
    {
        var (entity, rawToken) = BuildRefreshToken(userId, clientInstallId);
        db.RefreshTokens.Add(entity);
        await db.SaveChangesAsync(ct);
        return (rawToken, entity.ExpiresAt);
    }

    /// <summary>The token's row if it is known, unexpired and not revoked; null otherwise.</summary>
    public async Task<RefreshToken?> FindActiveRefreshTokenAsync(string rawToken, CancellationToken ct = default)
    {
        var hash = Hash(rawToken);
        var existing = await db.RefreshTokens.FirstOrDefaultAsync(r => r.TokenHash == hash, ct);
        return existing is { IsActive: true } ? existing : null;
    }

    /// <summary>
    /// Rotates an active refresh token: persists a replacement bound to the same install and revokes the
    /// original. The caller has checked everything else (account, install proof) first.
    /// </summary>
    public async Task<(string Token, DateTimeOffset ExpiresAt)> RotateAsync(RefreshToken existing, CancellationToken ct = default)
    {
        var (replacement, newRawToken) = BuildRefreshToken(existing.UserId, existing.ClientInstallId);
        existing.RevokedAt = DateTimeOffset.UtcNow;
        existing.ReplacedByTokenId = replacement.Id;

        db.RefreshTokens.Add(replacement);
        await db.SaveChangesAsync(ct);

        return (newRawToken, replacement.ExpiresAt);
    }

    public async Task RevokeAsync(string rawToken, CancellationToken ct = default)
    {
        var hash = Hash(rawToken);
        var existing = await db.RefreshTokens.FirstOrDefaultAsync(r => r.TokenHash == hash, ct);
        if (existing is not null && existing.RevokedAt is null)
        {
            existing.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    private (RefreshToken Entity, string RawToken) BuildRefreshToken(string userId, Guid? clientInstallId)
    {
        var lifetime = TimeSpan.FromDays(configuration.GetValue("Jwt:RefreshTokenLifetimeDays", 30));
        var rawToken = GenerateRawToken();
        var entity = new RefreshToken
        {
            UserId = userId,
            TokenHash = Hash(rawToken),
            ClientInstallId = clientInstallId,
            ExpiresAt = DateTimeOffset.UtcNow.Add(lifetime),
        };
        return (entity, rawToken);
    }

    private SymmetricSecurityKey GetSigningKey()
    {
        var keyValue = configuration["Jwt:SigningKey"];
        if (string.IsNullOrWhiteSpace(keyValue))
        {
            throw new InvalidOperationException(
                "Jwt:SigningKey is not configured. Set it via " +
                "'dotnet user-secrets set \"Jwt:SigningKey\" \"<base64 value>\"' in Development.");
        }

        return new SymmetricSecurityKey(Convert.FromBase64String(keyValue));
    }

    private static string GenerateRawToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static string Hash(string rawToken) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
}
