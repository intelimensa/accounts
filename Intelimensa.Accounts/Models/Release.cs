namespace Intelimensa.Accounts.Models;

/// <summary>
/// One published (or in-progress) AxoSync version. The binaries themselves live on disk (see
/// <see cref="ReleaseArtifact"/>); this row is the metadata and the publish state.
/// </summary>
public class Release
{
    public int Id { get; set; }

    /// <summary>Semver-style, e.g. "1.3.0" or "1.4.0-beta.1". Unique.</summary>
    public required string Version { get; set; }

    public string? Notes { get; set; }

    /// <summary>
    /// Clients older than this must update before they can be considered supported; the update
    /// check reports <c>mandatory</c> for them. Null = the update is optional. Same format as
    /// <see cref="Version"/>, and never higher than it.
    /// </summary>
    public string? MinimumVersion { get; set; }

    public ReleaseStatus Status { get; set; } = ReleaseStatus.Draft;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Set when the release first goes <see cref="ReleaseStatus.Published"/>.</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    public List<ReleaseArtifact> Artifacts { get; set; } = [];
}

public enum ReleaseStatus
{
    /// <summary>Being assembled by staff; not visible to participants.</summary>
    Draft,

    Published,

    /// <summary>Pulled after publishing (e.g. a bad build). Kept for history, not downloadable.</summary>
    Withdrawn,
}
