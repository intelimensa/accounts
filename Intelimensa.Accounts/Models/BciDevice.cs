namespace Intelimensa.Accounts.Models;

/// <summary>
/// A physical BCI unit Intelimensa has produced -- the production/inventory record, tracked from
/// manufacture onward, independent of whether it has been distributed to a participant yet. Not
/// to be confused with <see cref="Device"/>, which is the AxoSync client's computer/install, not
/// the headset itself.
/// </summary>
public class BciDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string SerialNumber { get; set; }

    /// <summary>Matches <see cref="Config.Key"/> vocabulary, e.g. "ms2", "ms5", "biosemi".</summary>
    public required string DeviceType { get; set; }

    public DateOnly ProducedAt { get; set; }

    public required string CurrentFirmwareVersion { get; set; }

    public DateTimeOffset? LastFirmwareUpdatedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public BciDeviceStatus Status { get; set; } = BciDeviceStatus.Reserved;

    /// <summary>
    /// Base64 SHA-256 of the normalized registration code (see <c>RegistrationCode</c>); the
    /// plaintext is only ever returned once, to the manufacturing station. Null for legacy and
    /// backfilled units, which therefore can't be registered while code checking is on.
    /// </summary>
    public string? RegistrationCodeHash { get; set; }

    public DateTimeOffset? ReservedAt { get; set; }

    /// <summary>The Manufacturer-role user who last reserved (or re-keyed) this unit.</summary>
    public string? ReservedByUserId { get; set; }

    public DateTimeOffset? ManufacturedAt { get; set; }

    public List<AccountDevice> AccountDevices { get; set; } = [];
}
