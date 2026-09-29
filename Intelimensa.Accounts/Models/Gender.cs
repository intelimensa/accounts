namespace Intelimensa.Accounts.Models;

public enum Gender
{
    Female,
    Male,
    NonBinary,
    PreferNotToSay,

    /// <summary>Paired with <see cref="StudyParticipation.GenderOtherDescription"/>.</summary>
    Other,
}
