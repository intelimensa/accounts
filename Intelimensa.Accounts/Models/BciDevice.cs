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

    public List<AccountDevice> AccountDevices { get; set; } = [];
}
