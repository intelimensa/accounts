using System.ComponentModel.DataAnnotations;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Email;
using Intelimensa.Accounts.Models;
using Intelimensa.Accounts.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Intelimensa.Accounts.Pages.Account;

public class ChangePasswordModel(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    ApplicationDbContext db,
    AccountMail accountMail,
    ILogger<ChangePasswordModel> log) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    [TempData]
    public string? Message { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
            return Page();

        var user = await userManager.GetUserAsync(User);
        if (user is null)
            return RedirectToPage("/Account/Login");

        var result = await userManager.ChangePasswordAsync(user, Input.CurrentPassword, Input.NewPassword);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
                ModelState.AddModelError(string.Empty, error.Code == "PasswordMismatch" ? "The current password is wrong." : error.Description);
            return Page();
        }

        // Other web sessions end with the new security stamp; this one stays signed in. AxoSync/Quarry sessions must log in again.
        await signInManager.RefreshSignInAsync(user);
        await UserSessions.RevokeRefreshTokensAsync(db, user.Id, DateTimeOffset.UtcNow);
        log.LogInformation("User {UserId} changed their password", user.Id);

        try
        {
            await accountMail.NotifyPasswordChangedAsync(user.Email!);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not send the password-changed notice to {Email}", user.Email);
        }

        Message = "Your password is changed. AxoSync installs are signed out and ask for the new password.";
        return RedirectToPage("/Account/Status");
    }

    public class InputModel
    {
        [Required, DataType(DataType.Password)]
        [Display(Name = "Current password")]
        public string CurrentPassword { get; set; } = string.Empty;

        [Required, DataType(DataType.Password)]
        [StringLength(100, MinimumLength = 12, ErrorMessage = "The {0} must be at least {2} characters long.")]
        [Display(Name = "New password")]
        public string NewPassword { get; set; } = string.Empty;

        [DataType(DataType.Password)]
        [Compare(nameof(NewPassword), ErrorMessage = "The password and confirmation password do not match.")]
        [Display(Name = "Confirm new password")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
