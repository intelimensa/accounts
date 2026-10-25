namespace Intelimensa.Accounts.Models;

/// <summary>
/// One config: its metadata, and once staff have uploaded it, its encrypted blob (v2 format, in
/// <c>IConfigStorage</c>) and content key (CEK), wrapped under the server's KEK. Configs aren't versioned:
/// a different config is a new row, and replacing the file is only for rotating its key or a fix.
/// See docs/config-management.md.
/// </summary>
public class Config
{
    public int Id { get; set; }

    /// <summary>Matches AxoSync's config stems, e.g. "ms2", "ms5", "biosemi".</summary>
    public required string Key { get; set; }

    public required string DisplayName { get; set; }

    /// <summary>The one config the staff assignment form pre-selects. At most one row has it.</summary>
    public bool IsDefault { get; set; }

    /// <summary>A retired config is neither delivered nor offered for new assignments.</summary>
    public DateTimeOffset? RetiredAt { get; set; }

    /// <summary>Opaque key understood by <c>IConfigStorage</c>; null until a file is uploaded.</summary>
    public string? BlobStorageKey { get; set; }

    /// <summary>Lowercase hex SHA-256 of the blob: its ETag, and part of every lease and wrapped key.</summary>
    public string? BlobSha256 { get; set; }

    public long? BlobSize { get; set; }

    /// <summary>The CEK, wrapped by <c>KeyEncryption</c> for purpose <c>config-cek/{Key}</c>.</summary>
    public string? WrappedCek { get; set; }

    /// <summary>Which KEK wrapped <see cref="WrappedCek"/>.</summary>
    public string? KekId { get; set; }

    /// <summary>When the live file was uploaded, and by whom.</summary>
    public DateTimeOffset? UploadedAt { get; set; }

    public string? UploadedByUserId { get; set; }

    /// <summary>When Staff (or a Publisher with auto-publish) made the live file live, and who.</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    public string? PublishedByUserId { get; set; }

    // A new file waits here until it is published, like a draft release: a Publisher (ConfigGen) or Staff upload it,
    // Staff review and publish it on /Staff/Configs. Publishing moves it over the live file (and so rotates the key).
    public string? PendingBlobStorageKey { get; set; }

    public string? PendingBlobSha256 { get; set; }

    public long? PendingBlobSize { get; set; }

    public string? PendingWrappedCek { get; set; }

    public string? PendingKekId { get; set; }

    public DateTimeOffset? PendingUploadedAt { get; set; }

    public string? PendingUploadedByUserId { get; set; }

    public bool HasFile => BlobStorageKey is not null && WrappedCek is not null && BlobSha256 is not null;

    public bool HasPendingFile => PendingBlobStorageKey is not null && PendingWrappedCek is not null && PendingBlobSha256 is not null;

    public bool IsDeliverable => HasFile && RetiredAt is null;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
