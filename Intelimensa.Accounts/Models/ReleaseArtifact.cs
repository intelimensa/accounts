namespace Intelimensa.Accounts.Models;

/// <summary>One downloadable build of a <see cref="Release"/> for a single platform.</summary>
public class ReleaseArtifact
{
    public int Id { get; set; }

    public int ReleaseId { get; set; }

    public Release? Release { get; set; }

    public ReleasePlatform Platform { get; set; }

    /// <summary>The name the participant's browser saves the file as.</summary>
    public required string FileName { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>Lowercase hex SHA-256 of the file, computed server-side at upload.</summary>
    public required string Sha256 { get; set; }

    /// <summary>
    /// Base64 DER ECDSA P-256 signature over the release statement (see <c>ReleaseSigning</c>),
    /// made offline by the publisher's private key. The server stores it and relays it; clients
    /// verify it against the public key built into AxoSync. Null for artifacts uploaded without one.
    /// </summary>
    public string? Signature { get; set; }

    /// <summary>
    /// The 32-byte secret compiled into this build, wrapped by <c>KeyEncryption</c> for purpose
    /// <c>release-secret/{version}/{platform}</c>. Config keys delivered to this build are wrapped with it,
    /// so only this build's code can open them. Null for builds published without one: they get no configs.
    /// </summary>
    public string? WrappedReleaseSecret { get; set; }

    /// <summary>Which KEK wrapped <see cref="WrappedReleaseSecret"/>.</summary>
    public string? ReleaseSecretKekId { get; set; }

    /// <summary>
    /// Set by staff to stop delivering config keys to this build (too old, or its secret is suspected
    /// leaked); its participants are told to update.
    /// </summary>
    public DateTimeOffset? ConfigDeliveryBlockedAt { get; set; }

    public bool CanReceiveConfigs => WrappedReleaseSecret is not null && ConfigDeliveryBlockedAt is null;

    /// <summary>Opaque key understood by <c>IReleaseStorage</c> -- not a filesystem path.</summary>
    public required string StorageKey { get; set; }

    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;
}
