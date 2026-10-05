namespace Intelimensa.Accounts.Models;

/// <summary>
/// One production-ready firmware image (an Intel HEX file) that the manufacturing station can
/// flash. Quarry lists the Published builds for the product it's working on and downloads the
/// chosen one, instead of flashing an arbitrary file from disk. The hex itself lives in
/// <c>IFirmwareStorage</c>; this row is the metadata and the publish state.
/// </summary>
public class FirmwareBuild
{
    public int Id { get; set; }

    /// <summary>The firmware family, a key of <c>Manufacturing:ProductCodes</c> (lowercase): "msv1", "msv2", "msv3".</summary>
    public required string DeviceType { get; set; }

    public FirmwareKind Kind { get; set; }

    /// <summary>The same string Quarry reports as <c>firmwareVersion</c>/<c>bootloaderVersion</c>. Unique per device type and kind.</summary>
    public required string Version { get; set; }

    public string? Notes { get; set; }

    public FirmwareBuildStatus Status { get; set; } = FirmwareBuildStatus.Draft;

    public required string FileName { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>Lowercase hex SHA-256 of the file, computed server-side at upload.</summary>
    public required string Sha256 { get; set; }

    /// <summary>Opaque key understood by <c>IFirmwareStorage</c> -- not a filesystem path.</summary>
    public required string StorageKey { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Set when the build first goes <see cref="FirmwareBuildStatus.Published"/>.</summary>
    public DateTimeOffset? PublishedAt { get; set; }
}

public enum FirmwareKind
{
    /// <summary>The application image. For MS-V1/MS-V2 this is the whole firmware.</summary>
    Application,

    /// <summary>The MS-V3 bootloader image. Written once at manufacturing.</summary>
    Bootloader,
}

public enum FirmwareBuildStatus
{
    /// <summary>Uploaded by staff but not yet offered to the station.</summary>
    Draft,

    Published,

    /// <summary>Pulled after publishing (e.g. a bad build). Kept for history, not listed or downloadable.</summary>
    Withdrawn,
}
