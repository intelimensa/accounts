namespace Intelimensa.Accounts.Models;

/// <summary>
/// Pairs an <see cref="Account"/> with a <see cref="BciDevice"/> it has registered. Many-to-many
/// by design -- a loaned unit can be reassigned or shared across accounts over the cohort's
/// lifetime, and an account may hold several units at once. Config assignment lives here rather
/// than on <see cref="Account"/>, because it's specific to a participant/hardware-unit/calibration
/// combination (see CLAUDE.md's config-assignment design), not the account as a whole.
/// </summary>
public class AccountDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public int AccountId { get; set; }

    public Account? Account { get; set; }

    public Guid BciDeviceId { get; set; }

    public BciDevice? BciDevice { get; set; }

    public int? AssignedConfigId { get; set; }

    public Config? AssignedConfig { get; set; }

    public DateTimeOffset RegisteredAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Staff-set when this account's access to the unit is revoked; registration never touches
    /// this, since the same serial may be registered to multiple accounts at once.
    /// </summary>
    public DateTimeOffset? UnassignedAt { get; set; }

    public bool IsActive => UnassignedAt is null;
}
