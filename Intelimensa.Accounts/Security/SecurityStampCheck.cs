using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;

namespace Intelimensa.Accounts.Security;

/// <summary>
/// Drops a web session whose user's security stamp has changed (a role granted or revoked, a
/// password change). <c>AddIdentityCore</c> doesn't wire this, and Identity's own
/// <c>SecurityStampValidator</c> also tries to sign out the two-factor cookie schemes, which this
/// app doesn't register. Checked on every request: one lookup by primary key.
/// </summary>
public static class SecurityStampCheck
{
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var userManager = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.GetUserAsync(context.Principal!);
        var claimType = userManager.Options.ClaimsIdentity.SecurityStampClaimType;

        if (user is null || user.SecurityStamp != context.Principal!.FindFirst(claimType)?.Value)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        }
    }
}
