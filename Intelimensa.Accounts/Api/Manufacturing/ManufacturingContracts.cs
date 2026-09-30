using Intelimensa.Accounts.Models;

namespace Intelimensa.Accounts.Api.Manufacturing;

public record ReserveUnitRequest(string DeviceType, string Region, string FirmwareVersion);

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

public record ConfirmUnitRequest(string SerialNumber, string RegistrationCode, string FirmwareVersion);

public record UnitRequest(string SerialNumber);

/// <summary>
/// <paramref name="Reason"/> is required, a string: "Relabel", "Reflash" or "Rework" (case-insensitive). The
/// <paramref name="FirmwareVersion"/> is the version the station intends to flash; it's recorded in
/// the unit's history but only applied to the unit at <c>confirm</c>.
/// </summary>
public record RekeyUnitRequest(string SerialNumber, string? Reason, string? Note, string FirmwareVersion);

public record FirmwareWriteRequest(string SerialNumber, string FirmwareVersion, string? Note);

public record UnitResponse(
    string SerialNumber,
    string SerialNumberLabel,
    string DeviceType,
    BciDeviceStatus Status,
    string FirmwareVersion,
    DateTimeOffset? ManufacturedAt,
    DateTimeOffset? FirmwareUpdatedAt);

public record ProductOption(string DeviceType, string ProductCode);

public record RegionOption(string Code, string Description);

public record ManufacturingOptionsResponse(
    List<ProductOption> Products,
    List<RegionOption> Regions,
    List<string> RekeyReasons);
