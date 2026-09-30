namespace Intelimensa.Accounts.Api.Devices;

/// <summary>
/// <paramref name="RegistrationCode"/> is read from the unit by AxoSync (or typed from the label as
/// a fallback); required for a new pairing while <c>Devices:RequireRegistrationCode</c> is on.
/// </summary>
public record RegisterDeviceRequest(string SerialNumber, string? RegistrationCode = null);

public record RegisterDeviceResponse(
    Guid AccountDeviceId,
    Guid BciDeviceId,
    string SerialNumber,
    string DeviceType,
    int? AssignedConfigId,
    DateTimeOffset RegisteredAt);
