namespace Intelimensa.Accounts.Models;

/// <summary>
/// Opt-in research enrollment for an <see cref="Account"/> -- 1:1, keyed by <see cref="AccountId"/>.
/// Presence of this row is the opt-in itself; accounts that never join the study have none. All
/// fields are filled in together at consent time (see CLAUDE.md's profile-wizard design) --
/// participants may still answer <see cref="Gender.PreferNotToSay"/> /
/// <see cref="DisabilityCategory.PreferNotToSay"/> rather than disclosing the actual value.
/// </summary>
public class StudyParticipation
{
    public int AccountId { get; set; }

    public Account? Account { get; set; }

    public DateTimeOffset ConsentGivenAt { get; set; } = DateTimeOffset.UtcNow;

    public required string ConsentVersion { get; set; }

    /// <summary>
    /// Withdrawal is prospective, not erasure: set on withdrawal, existing profile/telemetry rows
    /// are retained, and new telemetry is rejected once this is non-null.
    /// </summary>
    public DateTimeOffset? WithdrawnAt { get; set; }

    public required DateOnly DateOfBirth { get; set; }

    public Gender Gender { get; set; }

    public string? GenderOtherDescription { get; set; }

    public required string Region { get; set; }

    public DisabilityCategory Disability { get; set; }

    public string? DisabilityOtherDescription { get; set; }

    public bool IsActive => WithdrawnAt is null;
}
