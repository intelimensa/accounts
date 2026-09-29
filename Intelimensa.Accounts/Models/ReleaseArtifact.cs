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

    /// <summary>Opaque key understood by <c>IReleaseStorage</c> -- not a filesystem path.</summary>
    public required string StorageKey { get; set; }

    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;
}
