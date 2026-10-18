using System.Security.Claims;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Security;

/// <summary>
/// Whether the signed-in user's account passes <see cref="AccountPolicy.IsUsable"/>, for views that show or
/// hide links. Scoped, so the navbar and the page share one lookup per request. It only decides what to
/// <i>show</i>: the pages and endpoints behind the links check the policy themselves.
/// </summary>
public class AccountAccess(ApplicationDbContext db, UserManager<ApplicationUser> userManager)
{
    private Task<bool>? _usable;

    public Task<bool> IsUsableAsync(ClaimsPrincipal user) => _usable ??= LookUpAsync(user);

    private async Task<bool> LookUpAsync(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true)
            return false;

        var userId = userManager.GetUserId(user);
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.UserId == userId);
        return AccountPolicy.IsUsable(account);
    }
}
