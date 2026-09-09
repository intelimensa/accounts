namespace Intelimensa.Accounts.Models;

/// <summary>
/// The entitlement record for a user -- 1:1 with an <see cref="ApplicationUser"/>. Config
/// assignment is staff-only (see CLAUDE.md); a new account starts with no assigned config.
/// </summary>
public class Account
{
    public int Id { get; set; }

    public required string UserId { get; set; }

    public ApplicationUser? User { get; set; }

    public int? AssignedConfigId { get; set; }

    public Config? AssignedConfig { get; set; }

    public AccountStatus Status { get; set; } = AccountStatus.Active;

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<Device> Devices { get; set; } = [];
}
