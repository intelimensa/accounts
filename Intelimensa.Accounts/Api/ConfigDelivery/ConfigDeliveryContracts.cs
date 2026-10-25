namespace Intelimensa.Accounts.Api.ConfigDelivery;

/// <summary>
/// What <c>GET /api/devices/{serial}/config</c> returns: the unit's config, its key wrapped for the calling install
/// and build, and a signed offline lease. The blob itself comes from <see cref="BlobUrl"/>; it is useless without
/// the key. Binary values are base64.
/// </summary>
public record ConfigDeliveryResponse(
    string ConfigKey,
    string DisplayName,
    string BlobSha256,
    long BlobSize,
    string BlobUrl,
    WrappedCekResponse WrappedCek,
    LeaseResponse Lease,
    string LeaseSignature);

/// <summary>The CEK wrap (<c>CekWrap</c>): the server's ephemeral public key (SPKI), the GCM nonce, ciphertext and tag.</summary>
public record WrappedCekResponse(string EphemeralKey, string Nonce, string Ciphertext, string Tag);

/// <summary>
/// The lease's terms, exactly as signed (<c>OfflineLease</c>): the times are strings in the statement's own format
/// (<c>2026-11-01T12:00:00Z</c>), so a client rebuilds the signed text from these fields without formatting a date.
/// </summary>
public record LeaseResponse(
    int AccountId,
    Guid ClientInstallId,
    string SerialNumber,
    string ConfigKey,
    string BlobSha256,
    string IssuedAt,
    string NotAfter);

/// <summary>Body of <c>PUT /api/publishing/configs/{key}</c>: creates the config, or renames it.</summary>
public record ConfigDraftRequest(string? DisplayName);

/// <summary>A config as the publisher API reports it: its live file (what participants get) and any pending one.</summary>
public record PublishedConfigResponse(
    string Key,
    string DisplayName,
    bool IsDefault,
    bool Retired,
    ConfigFileResponse? Live,
    ConfigFileResponse? Pending);

public record ConfigFileResponse(string Sha256, long SizeBytes, DateTimeOffset? UploadedAt);
