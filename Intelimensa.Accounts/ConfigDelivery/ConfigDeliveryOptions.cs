namespace Intelimensa.Accounts.ConfigDelivery;

/// <summary>
/// The <c>Configs</c> section. The two secrets are what turn config delivery on: without them the server
/// still runs (logins, updates, manufacturing), and the config endpoints and uploads answer that delivery
/// is unavailable. See <c>docs/config-management.md</c>, "Server key management".
/// </summary>
public class ConfigDeliveryOptions
{
    /// <summary>Base64, 32 bytes: wraps every config's CEK and every build's release secret in the DB. A secret.</summary>
    public string? KeyEncryptionKey { get; set; }

    /// <summary>Recorded next to everything the KEK wraps, so the KEK can be rotated later.</summary>
    public string KeyEncryptionKeyId { get; set; } = "kek-1";

    /// <summary>Base64 PKCS#8 DER of the P-256 private key that signs offline leases. A secret.</summary>
    public string? LeaseSigningKey { get; set; }

    /// <summary>Where uploaded config blobs live: outside the web root and the deploy directory.</summary>
    public string StoragePath { get; set; } = "config-files";

    public int OfflineLeaseDays { get; set; } = 7;

    /// <summary>Caps a per-account override, so a typo can't grant a year.</summary>
    public int MaxOfflineLeaseDays { get; set; } = 30;

    /// <summary>One per target a participant might use (desktop, laptop, phone, tablet, browser) plus a spare.</summary>
    public int MaxInstallsPerAccount { get; set; } = 6;

    /// <summary>An install not seen for this long stops counting toward the limit and is revoked.</summary>
    public int InstallIdleDays { get; set; } = 60;

    /// <summary>Installs registered within a day that make the server log a warning for staff.</summary>
    public int InstallBurstWarning { get; set; } = 3;

    /// <summary>
    /// Base64, 32 bytes: the release secret of Debug builds, which ask with <c>version=development</c>.
    /// Development servers only; the server refuses to start with it outside Development.
    /// </summary>
    public string? DevelopmentReleaseSecret { get; set; }

    public const string DevelopmentVersion = "development";
}
