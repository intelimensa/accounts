using Intelimensa.Accounts.Models;

namespace Intelimensa.Accounts.Security;

/// <summary>
/// The account-usability rule enforced everywhere an authenticated account tries to obtain or use
/// access: login, refresh, and device registration all gate on this rather than duplicating it.
/// </summary>
public static class AccountPolicy
{
    public static bool IsUsable(Account? account) =>
        account is not null &&
        account.Status == AccountStatus.Active &&
        (account.ExpiresAt is null || account.ExpiresAt > DateTimeOffset.UtcNow);
}
