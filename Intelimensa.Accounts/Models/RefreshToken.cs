namespace Intelimensa.Accounts.Models;

/// <summary>
/// An issued refresh token. Only <see cref="TokenHash"/> (SHA-256 of the raw token) is stored --
/// same "never store the verifier directly" approach as password hashing. A token issued at a login
/// that registered a <see cref="ClientInstall"/> is bound to it (<see cref="ClientInstallId"/>): refreshing
/// it then needs that install's proof, and its replacement stays bound. Tokens from logins without an
/// install (Quarry, the publish scripts, older AxoSync builds) are unbound and can't fetch configs.
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

    public Guid? ClientInstallId { get; set; }

    public ClientInstall? ClientInstall { get; set; }

    public bool IsActive => RevokedAt is null && ExpiresAt > DateTimeOffset.UtcNow;
}
