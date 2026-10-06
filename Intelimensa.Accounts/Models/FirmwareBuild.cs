namespace Intelimensa.Accounts.Models;

/// <summary>
/// One production-ready firmware image (an Intel HEX file) that the manufacturing station can
/// flash. Quarry lists the Published builds for the product it's working on and downloads the
/// chosen one, instead of flashing an arbitrary file from disk. The hex itself lives in
/// <c>IFirmwareStorage</c>; this row is the metadata and the publish state. The versions are read
/// from the file at upload (<c>FirmwareImage.Describe</c>), never typed.
/// </summary>
public class FirmwareBuild
{
    public int Id { get; set; }

    /// <summary>The firmware family, a key of <c>Manufacturing:ProductCodes</c> (lowercase): "msv1", "msv2", "msv3".</summary>
    public required string DeviceType { get; set; }

    public FirmwareKind Kind { get; set; }

    /// <summary>The app's version, as read from the image: what Quarry reports as <c>firmwareVersion</c>. Unique per device type and kind.</summary>
    public required string Version { get; set; }

    /// <summary>The bootloader's version, read from a <see cref="FirmwareKind.Factory"/> image (what Quarry reports as <c>bootloaderVersion</c>); null otherwise.</summary>
    public string? BootloaderVersion { get; set; }

    public string? Notes { get; set; }

    public FirmwareBuildStatus Status { get; set; } = FirmwareBuildStatus.Draft;

    public required string FileName { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>Lowercase hex SHA-256 of the file, computed server-side at upload.</summary>
    public required string Sha256 { get; set; }

    /// <summary>Opaque key understood by <c>IFirmwareStorage</c> -- not a filesystem path.</summary>
    public required string StorageKey { get; set; }

    /// <summary>What a programmer can write to a new or reworked unit: the whole firmware for MS-V1/MS-V2, the factory image for MS-V3.</summary>
    public bool FlashableByProgrammer => DeviceType == "msv3" ? Kind == FirmwareKind.Factory : Kind == FirmwareKind.Application;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Set when the build first goes <see cref="FirmwareBuildStatus.Published"/>.</summary>
    public DateTimeOffset? PublishedAt { get; set; }
}

public enum FirmwareKind
{
    /// <summary>
    /// An application image on its own. For MS-V1/MS-V2 that is the whole firmware, written with a
    /// programmer; for MS-V3 it's the app-only image an update over USB sends.
    /// </summary>
    Application,

    /// <summary>
    /// An MS-V3 factory image: bootloader plus app, what a programmer writes to a new unit. A
    /// bootloader on its own isn't a catalog item: it looks like any single image to the programmer.
    /// </summary>
    Factory,
}

public enum FirmwareBuildStatus
{
    /// <summary>Uploaded by staff but not yet offered to the station.</summary>
    Draft,

    Published,

    /// <summary>Pulled after publishing (e.g. a bad build). Kept for history, not listed or downloadable.</summary>
    Withdrawn,
}
