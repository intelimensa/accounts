using System.ComponentModel.DataAnnotations;
using Intelimensa.Accounts.Email;
using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Intelimensa.Accounts.Pages.Account;

public class ChangeEmailModel(
    UserManager<ApplicationUser> userManager,
    AccountMail accountMail,
    ILogger<ChangeEmailModel> log) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string CurrentEmail { get; private set; } = string.Empty;

    public string? SentTo { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
            return RedirectToPage("/Account/Login");
        CurrentEmail = user.Email ?? string.Empty;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
            return RedirectToPage("/Account/Login");
        CurrentEmail = user.Email ?? string.Empty;

        if (!ModelState.IsValid)
            return Page();

        if (!await userManager.CheckPasswordAsync(user, Input.Password))
        {
            ModelState.AddModelError(string.Empty, "The password is wrong.");
            return Page();
        }

        if (string.Equals(Input.NewEmail, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(string.Empty, "That is already your email address.");
            return Page();
        }

        if (!accountMail.TryBeginEmailChange(user.Id, DateTimeOffset.UtcNow))
        {
            ModelState.AddModelError(string.Empty, "A confirmation email was sent a moment ago. Check the inbox of the new address, or try again in a couple of minutes.");
            return Page();
        }

        // An address that belongs to another account gets no email, but the answer is the same, so this form
        // can't be used to look up who has an account. (ConfirmEmailChange checks again when the link is opened.)
        var taken = await userManager.FindByEmailAsync(Input.NewEmail);
        if (taken is null)
        {
            try
            {
                await accountMail.SendEmailChangeAsync(user, Input.NewEmail, $"{Request.Scheme}://{Request.Host}");
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Could not send the email-change link to {Email}", Input.NewEmail);
                ModelState.AddModelError(string.Empty, "We couldn't send the email right now. Please try again later.");
                return Page();
            }
        }

        SentTo = Input.NewEmail;
        return Page();
    }

    public class InputModel
    {
        [Required, EmailAddress]
        [Display(Name = "New email address")]
        public string NewEmail { get; set; } = string.Empty;

        [Required, DataType(DataType.Password)]
        [Display(Name = "Current password")]
        public string Password { get; set; } = string.Empty;
    }
}
