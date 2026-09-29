namespace Intelimensa.Accounts.Models;

public enum DisabilityCategory
{
    None,
    Motor,
    Visual,
    Hearing,
    Cognitive,
    PreferNotToSay,

    /// <summary>Paired with <see cref="StudyParticipation.DisabilityOtherDescription"/>.</summary>
    Other,
}
