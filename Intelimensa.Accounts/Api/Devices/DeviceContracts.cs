namespace Intelimensa.Accounts.Api.Devices;

public record RegisterDeviceRequest(string SerialNumber);

public record RegisterDeviceResponse(
    Guid AccountDeviceId,
    Guid BciDeviceId,
    string SerialNumber,
    string DeviceType,
    int? AssignedConfigId,
    DateTimeOffset RegisteredAt);
