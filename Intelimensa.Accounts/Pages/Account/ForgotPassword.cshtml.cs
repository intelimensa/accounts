using System.ComponentModel.DataAnnotations;
using Intelimensa.Accounts.Email;
using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Intelimensa.Accounts.Pages.Account;

public class ForgotPasswordModel(
    UserManager<ApplicationUser> userManager,
    AccountMail accountMail,
    ILogger<ForgotPasswordModel> log) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool Sent { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
            return Page();

        // The answer is the same whether or not the address has an account, so this page can't be used to find out.
        var user = await userManager.FindByEmailAsync(Input.Email);
        if (user is not null && accountMail.TryBeginReset(user.Id, DateTimeOffset.UtcNow))
        {
            try
            {
                await accountMail.SendPasswordResetAsync(user, $"{Request.Scheme}://{Request.Host}");
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Could not send the password reset email to {Email}", user.Email);
            }
        }

        Sent = true;
        return Page();
    }

    public class InputModel
    {
        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;
    }
}
