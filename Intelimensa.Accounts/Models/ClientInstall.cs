namespace Intelimensa.Accounts.Models;

/// <summary>
/// One registered installation of AxoSync (one instance slot on one machine or app install), owned by an
/// <see cref="Account"/>. Not a <see cref="BciDevice"/>: in this service a device is an Intelimensa unit.
/// Created only at a password login that carries the install's public keys (<c>ClientInstallRegistry</c>);
/// tokens issued then are bound to it, and every request on them is signed with its signing key
/// (<c>InstallProof</c>). Config keys are wrapped to its agreement key. See docs/config-management.md, section 1.
/// </summary>
public class ClientInstall
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public int AccountId { get; set; }

    public Account? Account { get; set; }

    /// <summary>Base64 SPKI of the P-256 key that signs this install's requests.</summary>
    public required string SigningKey { get; set; }

    /// <summary>Lowercase hex SHA-256 of <see cref="SigningKey"/>'s DER; unique per account.</summary>
    public required string KeyThumbprint { get; set; }

    /// <summary>Base64 SPKI of the P-256 key config keys are wrapped to (ECDH).</summary>
    public required string AgreementKey { get; set; }

    public ClientPlatform Platform { get; set; }

    /// <summary>What the participant sees on their account page, e.g. the computer's name.</summary>
    public string? Name { get; set; }

    /// <summary>The AxoSync version that last signed in with this install.</summary>
    public string? AppVersion { get; set; }

    /// <summary>AxoSync's instance slot on that machine (several instances may run side by side).</summary>
    public int? Slot { get; set; }

    public DateTimeOffset RegisteredAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>Set when the participant or staff remove it, or when it has been idle too long.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    public DateTimeOffset LastActivity => LastSeenAt ?? RegisteredAt;

    /// <summary>Not revoked and seen within the idle window: counts toward the limit and may be used.</summary>
    public bool IsActive(DateTimeOffset now, TimeSpan idleWindow) => RevokedAt is null && now - LastActivity <= idleWindow;
}
