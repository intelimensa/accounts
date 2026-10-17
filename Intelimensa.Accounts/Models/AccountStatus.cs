namespace Intelimensa.Accounts.Models;

public enum AccountStatus
{
    Active,
    Revoked,

    /// <summary>Signed up, not yet approved by staff. Not usable. Stored as 2 so existing rows keep their values.</summary>
    Pending = 2,
}
