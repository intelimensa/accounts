using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using AccountEntity = Intelimensa.Accounts.Models.Account;

namespace Intelimensa.Accounts.Pages.Account;

public class StatusModel(UserManager<ApplicationUser> userManager, ApplicationDbContext db) : PageModel
{
    public string Email { get; private set; } = string.Empty;

    public AccountEntity? Account { get; private set; }

    public string? AssignedConfigName { get; private set; }

    public async Task OnGetAsync()
    {
        var userId = userManager.GetUserId(User);
        Email = User.Identity?.Name ?? string.Empty;

        Account = await db.Accounts
            .Include(a => a.AssignedConfig)
            .FirstOrDefaultAsync(a => a.UserId == userId);

        AssignedConfigName = Account?.AssignedConfig?.DisplayName;
    }
}
