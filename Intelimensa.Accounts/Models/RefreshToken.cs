namespace Intelimensa.Accounts.Models;

/// <summary>
/// An issued refresh token. Only <see cref="TokenHash"/> (SHA-256 of the raw token) is stored --
/// same "never store the verifier directly" approach as password hashing. Not yet tied to a
/// <see cref="Device"/>; that binding is future work once device registration exists.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string UserId { get; set; }

    public ApplicationUser? User { get; set; }

    public required string TokenHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public Guid? ReplacedByTokenId { get; set; }

    public bool IsActive => RevokedAt is null && ExpiresAt > DateTimeOffset.UtcNow;
}
