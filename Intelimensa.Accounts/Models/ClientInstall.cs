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

    /// <summary>
    /// The AxoSync version this install last reported: at password login, and afterwards on every update check
    /// (<c>current</c>) and config delivery (<c>version</c>), since an updated app keeps running on its refresh token
    /// and doesn't sign in with the password again.
    /// </summary>
    public string? AppVersion { get; set; }

    /// <summary>AxoSync's instance slot on that machine (several instances may run side by side).</summary>
    public int? Slot { get; set; }

    public DateTimeOffset RegisteredAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>Set when the participant or staff remove it, or when it has been idle too long.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Records the version the app reports; true if it changed (the caller saves). Blank or oversized values are ignored.</summary>
    public bool RecordVersion(string? version)
    {
        version = version?.Trim();
        if (string.IsNullOrEmpty(version) || version.Length > 50 || version == AppVersion) return false;
        AppVersion = version;
        return true;
    }

    public DateTimeOffset LastActivity => LastSeenAt ?? RegisteredAt;

    /// <summary>Not revoked and seen within the idle window: counts toward the limit and may be used.</summary>
    public bool IsActive(DateTimeOffset now, TimeSpan idleWindow) => RevokedAt is null && now - LastActivity <= idleWindow;
}
