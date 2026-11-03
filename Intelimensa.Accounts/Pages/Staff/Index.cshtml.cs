using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Pages.Staff;

public class IndexModel(ApplicationDbContext db) : PageModel
{
    public int PendingAccounts { get; private set; }

    public async Task OnGetAsync()
    {
        PendingAccounts = await db.Accounts.CountAsync(a => a.Status == AccountStatus.Pending);
    }
}
