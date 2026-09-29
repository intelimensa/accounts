namespace Intelimensa.Accounts.Models;

/// <summary>
/// The entitlement record for a user -- 1:1 with an <see cref="ApplicationUser"/>. Access control
/// (<see cref="Status"/>/<see cref="ExpiresAt"/>) lives here; config assignment does not -- it's
/// per registered <see cref="BciDevice"/> (see <see cref="AccountDevice.AssignedConfigId"/>),
/// since one account can hold several units, each potentially running a different config.
/// </summary>
public class Account
{
    public int Id { get; set; }

    public required string UserId { get; set; }

    public ApplicationUser? User { get; set; }

    public AccountStatus Status { get; set; } = AccountStatus.Active;

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<Device> Devices { get; set; } = [];

    public List<AccountDevice> AccountDevices { get; set; } = [];

    /// <summary>Null unless this account has opted into the research study.</summary>
    public StudyParticipation? StudyParticipation { get; set; }
}
