using Intelimensa.Accounts.Models;

namespace Intelimensa.Accounts.Api.Manufacturing;

public record ReserveUnitRequest(string DeviceType, string Region, string FirmwareVersion);

/// <summary>Returned by reserve and rekey. The only time the plaintext code is ever exposed.</summary>
public record IssuedUnitResponse(
    string SerialNumber,
    string RegistrationCode,
    string RegistrationCodeLabel,
    string DeviceType,
    DateTimeOffset ReservedAt);

public record ConfirmUnitRequest(string SerialNumber, string RegistrationCode, string FirmwareVersion);

public record UnitRequest(string SerialNumber);

public record UnitResponse(
    string SerialNumber,
    string DeviceType,
    BciDeviceStatus Status,
    string FirmwareVersion,
    DateTimeOffset? ManufacturedAt);
