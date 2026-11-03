using System.ComponentModel.DataAnnotations;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Email;
using Intelimensa.Accounts.Models;
using Intelimensa.Accounts.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Intelimensa.Accounts.Pages.Account;

public class ResetPasswordModel(
    UserManager<ApplicationUser> userManager,
    ApplicationDbContext db,
    AccountMail accountMail,
    ILogger<ResetPasswordModel> log) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>The link was missing its parts; the form isn't shown.</summary>
    public bool InvalidLink { get; private set; }

    public bool Done { get; private set; }

    public void OnGet(string? userId, string? code)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(code) || !EmailConfirmation.TryDecodeToken(code, out _))
        {
            InvalidLink = true;
            return;
        }
        Input = new InputModel { UserId = userId, Code = code };
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
            return Page();

        // Every failure looks alike: an unknown user, a bad token and an expired token are not told apart.
        var user = await userManager.FindByIdAsync(Input.UserId);
        if (user is null || !EmailConfirmation.TryDecodeToken(Input.Code, out var token))
        {
            InvalidLink = true;
            return Page();
        }

        var result = await userManager.ResetPasswordAsync(user, token, Input.Password);
        if (!result.Succeeded)
        {
            // Password-policy errors can be fixed in place; anything else means the link is no good.
            var policy = result.Errors.Where(e => e.Code.StartsWith("Password", StringComparison.Ordinal)).ToList();
            if (policy.Count == 0)
            {
                InvalidLink = true;
                return Page();
            }
            foreach (var error in policy)
                ModelState.AddModelError(string.Empty, error.Description);
            return Page();
        }

        // Receiving the link proves control of the mailbox, and a locked-out user who forgot the password is the common case.
        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            await userManager.UpdateAsync(user);
        }
        await userManager.ResetAccessFailedCountAsync(user);
        await userManager.SetLockoutEndDateAsync(user, null);
        await UserSessions.RevokeRefreshTokensAsync(db, user.Id, DateTimeOffset.UtcNow);
        log.LogInformation("Password of user {UserId} reset by email link", user.Id);

        try
        {
            await accountMail.NotifyPasswordChangedAsync(user.Email!);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not send the password-changed notice to {Email}", user.Email);
        }

        Done = true;
        return Page();
    }

    public class InputModel
    {
        [Required]
        public string UserId { get; set; } = string.Empty;

        [Required]
        public string Code { get; set; } = string.Empty;

        [Required, DataType(DataType.Password)]
        [StringLength(100, MinimumLength = 12, ErrorMessage = "The {0} must be at least {2} characters long.")]
        [Display(Name = "New password")]
        public string Password { get; set; } = string.Empty;

        [DataType(DataType.Password)]
        [Compare(nameof(Password), ErrorMessage = "The password and confirmation password do not match.")]
        [Display(Name = "Confirm new password")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
