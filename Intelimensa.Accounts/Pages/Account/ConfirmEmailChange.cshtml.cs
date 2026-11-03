using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Email;
using Intelimensa.Accounts.Models;
using Intelimensa.Accounts.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Intelimensa.Accounts.Pages.Account;

public class ConfirmEmailChangeModel(
    UserManager<ApplicationUser> userManager,
    ApplicationDbContext db,
    AccountMail accountMail,
    ILogger<ConfirmEmailChangeModel> log) : PageModel
{
    public bool Changed { get; private set; }

    public string? NewEmail { get; private set; }

    public async Task OnGetAsync(string? userId, string? email, string? code)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(email) || string.IsNullOrEmpty(code))
            return;

        var user = await userManager.FindByIdAsync(userId);
        if (user is null || !EmailConfirmation.TryDecodeToken(code, out var token))
            return;

        var oldEmail = user.Email!;
        // The address is the sign-in name here (UserName == Email); both move together. The token is bound to the new
        // address, and RequireUniqueEmail makes a taken address fail.
        var result = await userManager.ChangeEmailAsync(user, email, token);
        if (!result.Succeeded)
            return;
        var named = await userManager.SetUserNameAsync(user, email);
        if (!named.Succeeded)
        {
            log.LogError("User {UserId}: email changed but the user name could not follow: {Errors}",
                user.Id, string.Join("; ", named.Errors.Select(e => e.Description)));
            return;
        }

        await UserSessions.RevokeRefreshTokensAsync(db, user.Id, DateTimeOffset.UtcNow);
        log.LogInformation("User {UserId} changed their email address", user.Id);

        try
        {
            await accountMail.NotifyEmailChangedAsync(oldEmail, email);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not send the email-changed notice to {Email}", oldEmail);
        }

        Changed = true;
        NewEmail = email;
    }
}
