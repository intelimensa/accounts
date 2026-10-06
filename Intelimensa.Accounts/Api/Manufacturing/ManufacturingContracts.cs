using Intelimensa.Accounts.Models;

namespace Intelimensa.Accounts.Api.Manufacturing;

/// <summary>
/// <paramref name="BootloaderVersion"/> is optional everywhere it appears: send it for units that have a
/// bootloader (MS-V3, taken from the factory image), omit it for those that don't (MS-V2).
/// </summary>
public record ReserveUnitRequest(string DeviceType, string Region, string FirmwareVersion, string? BootloaderVersion = null);

/// <summary>
/// Returned by reserve and rekey. The only time the plaintext code is ever exposed.
/// <c>SerialNumber</c> is the canonical dashless form to store and send; <c>SerialNumberLabel</c>
/// is the dashed form for human-facing display (labels), like <c>RegistrationCodeLabel</c>.
/// </summary>
public record IssuedUnitResponse(
    string SerialNumber,
    string SerialNumberLabel,
    string RegistrationCode,
    string RegistrationCodeLabel,
    string DeviceType,
    DateTimeOffset ReservedAt);

public record ConfirmUnitRequest(string SerialNumber, string RegistrationCode, string FirmwareVersion, string? BootloaderVersion = null);

public record UnitRequest(string SerialNumber);

/// <summary>
/// <paramref name="Reason"/> is required, a string: "Relabel", "Reflash" or "Rework" (case-insensitive). The
/// <paramref name="FirmwareVersion"/> is the version the station intends to flash; it's recorded in
/// the unit's history but only applied to the unit at <c>confirm</c>.
/// </summary>
public record RekeyUnitRequest(string SerialNumber, string? Reason, string? Note, string FirmwareVersion, string? BootloaderVersion = null);

public record FirmwareWriteRequest(string SerialNumber, string FirmwareVersion, string? Note, string? BootloaderVersion = null);

public record UnitResponse(
    string SerialNumber,
    string SerialNumberLabel,
    string DeviceType,
    BciDeviceStatus Status,
    string FirmwareVersion,
    string? BootloaderVersion,
    DateTimeOffset? ManufacturedAt,
    DateTimeOffset? FirmwareUpdatedAt);

public record ProductOption(string DeviceType, string ProductCode);

public record RegionOption(string Code, string Description);

public record ManufacturingOptionsResponse(
    List<ProductOption> Products,
    List<RegionOption> Regions,
    List<string> RekeyReasons);

/// <summary>
/// One flashable build in the firmware catalog, with versions read from the image itself.
/// <c>Kind</c> is <c>Application</c> (MS-V1/MS-V2's whole firmware, or an MS-V3 app on its own for a
/// USB update) or <c>Factory</c> (an MS-V3 bootloader + app image for a programmer). <c>Version</c> is
/// what the station reports as <c>firmwareVersion</c>; <c>BootloaderVersion</c> (factory images only)
/// is what it reports as <c>bootloaderVersion</c>. <c>Sha256</c> is lowercase hex of the file the
/// download returns.
/// </summary>
public record FirmwareBuildResponse(
    int Id,
    string DeviceType,
    string Kind,
    string Version,
    string? BootloaderVersion,
    string? Notes,
    string FileName,
    long SizeBytes,
    string Sha256,
    DateTimeOffset PublishedAt);
