using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Pages.Staff;

public class IndexModel(ApplicationDbContext db) : PageModel
{
    public List<AccountRow> Accounts { get; private set; } = [];

    public List<Config> Configs { get; private set; } = [];

    public string? StatusMessage { get; set; }

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostUpdateAsync(
        int accountId,
        int? assignedConfigId,
        AccountStatus status,
        string? expiresAtDate)
    {
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId);
        if (account is not null)
        {
            account.AssignedConfigId = assignedConfigId;
            account.Status = status;
            account.ExpiresAt = string.IsNullOrWhiteSpace(expiresAtDate)
                ? null
                : new DateTimeOffset(DateOnly.Parse(expiresAtDate).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

            await db.SaveChangesAsync();
            StatusMessage = "Account updated.";
        }

        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        Configs = await db.Configs.OrderBy(c => c.DisplayName).ToListAsync();

        Accounts = await db.Accounts
            .Include(a => a.User)
            .Include(a => a.AssignedConfig)
            .OrderBy(a => a.User!.Email)
            .Select(a => new AccountRow(
                a.Id,
                a.User!.Email ?? "(no email)",
                a.Status,
                a.ExpiresAt,
                a.AssignedConfigId))
            .ToListAsync();
    }

    public record AccountRow(int AccountId, string Email, AccountStatus Status, DateTimeOffset? ExpiresAt, int? AssignedConfigId);
}
