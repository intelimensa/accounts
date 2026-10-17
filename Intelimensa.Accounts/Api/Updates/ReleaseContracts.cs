namespace Intelimensa.Accounts.Api.Updates;

/// <summary>
/// Answer to the update check. <see cref="UpdateAvailable"/> false means the caller is current (or
/// nothing is published for its platform); the remaining fields are then null.
/// <see cref="Mandatory"/> is true when the caller's version is below the offered release's
/// <see cref="MinimumVersion"/>. The client must still verify the artifact's signature itself.
/// </summary>
public record UpdateCheckResponse(
    bool UpdateAvailable,
    bool Mandatory,
    string? Version,
    string? Notes,
    string? MinimumVersion,
    DateTimeOffset? PublishedAt,
    UpdateArtifact? Artifact);

/// <param name="DownloadUrl">Relative to the API base address; needs the same bearer token as the check.</param>
/// <param name="Signature">See <c>ReleaseSigning</c>; null only if the server has no signing key configured.</param>
public record UpdateArtifact(string FileName, long SizeBytes, string Sha256, string? Signature, string DownloadUrl);

public record DraftReleaseRequest(string? Notes, string? MinimumVersion);

/// <remarks><see cref="Status"/> and <c>Platform</c> are enum names ("Published", "MacOSArm64"), not numbers.</remarks>
public record PublishedReleaseResponse(
    string Version,
    string Status,
    string? Notes,
    string? MinimumVersion,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<PublishedArtifactResponse> Artifacts);

public record PublishedArtifactResponse(
    string Platform, string FileName, long SizeBytes, string Sha256, bool Signed);
