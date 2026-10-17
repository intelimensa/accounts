using Intelimensa.Accounts.Email;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Intelimensa.Accounts.Pages.Account;

public class ConfirmEmailModel(EmailConfirmation emailConfirmation) : PageModel
{
    public bool Confirmed { get; private set; }

    public async Task OnGetAsync(string? userId, string? code)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(code))
            return;

        Confirmed = (await emailConfirmation.ConfirmAsync(userId, code)).Succeeded;
    }
}
