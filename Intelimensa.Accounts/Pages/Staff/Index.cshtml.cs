using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Pages.Staff;

public class IndexModel(ApplicationDbContext db) : PageModel
{
    public List<AccountRow> Accounts { get; private set; } = [];

    // TempData: the handlers redirect, so a plain property would be lost before it is shown.
    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    private const string UnconfirmedMessage = "Can't activate an account whose email address isn't confirmed.";

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostUpdateAsync(
        int accountId,
        AccountStatus status,
        string? expiresAtDate)
    {
        var account = await db.Accounts.Include(a => a.User).FirstOrDefaultAsync(a => a.Id == accountId);
        if (account is not null)
        {
            if (status == AccountStatus.Active && account.Status != AccountStatus.Active && !account.User!.EmailConfirmed)
            {
                ErrorMessage = UnconfirmedMessage;
                return RedirectToPage();
            }

            account.Status = status;
            account.ExpiresAt = string.IsNullOrWhiteSpace(expiresAtDate)
                ? null
                : new DateTimeOffset(DateOnly.Parse(expiresAtDate).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

            await db.SaveChangesAsync();
            StatusMessage = "Account updated.";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostApproveAsync(int accountId, string? expiresAtDate)
    {
        var account = await db.Accounts.Include(a => a.User).FirstOrDefaultAsync(a => a.Id == accountId);
        if (account is { Status: AccountStatus.Pending })
        {
            if (!account.User!.EmailConfirmed)
            {
                ErrorMessage = UnconfirmedMessage;
                return RedirectToPage();
            }

            account.Status = AccountStatus.Active;
            account.ExpiresAt = string.IsNullOrWhiteSpace(expiresAtDate)
                ? null
                : new DateTimeOffset(DateOnly.Parse(expiresAtDate).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

            await db.SaveChangesAsync();
            StatusMessage = "Account approved.";
        }

        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        Accounts = await db.Accounts
            .Include(a => a.User)
            .OrderBy(a => a.Status == AccountStatus.Pending ? 0 : 1)
            .ThenBy(a => a.User!.Email)
            .Select(a => new AccountRow(
                a.Id,
                a.User!.Email ?? "(no email)",
                a.Status,
                a.User!.EmailConfirmed,
                a.ExpiresAt,
                a.AccountDevices.Count))
            .ToListAsync();
    }

    public record AccountRow(int AccountId, string Email, AccountStatus Status, bool EmailConfirmed, DateTimeOffset? ExpiresAt, int RegisteredDeviceCount);
}
