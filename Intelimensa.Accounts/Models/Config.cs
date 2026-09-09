namespace Intelimensa.Accounts.Models;

/// <summary>
/// Config metadata. The encrypted payload itself is not modeled yet -- that's a
/// config-delivery-phase decision (envelope encryption / CEK storage), not a data-model one.
/// </summary>
public class Config
{
    public int Id { get; set; }

    /// <summary>Matches AxoSync's config stems, e.g. "ms2", "ms5", "biosemi".</summary>
    public required string Key { get; set; }

    public required string DisplayName { get; set; }

    public int CurrentVersion { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
