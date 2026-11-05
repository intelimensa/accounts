using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Manufacturing;
using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Pages.Manufacturing;

/// <summary>
/// Base of the production checklist pages (a unit's checklist, mounting types, electrode sets). Manufacturer
/// role only (the folder policy), and the account is re-checked as usable on every action, like the other
/// manufacturing pages. Staff have no access to these for now.
/// </summary>
public abstract class ProductionPageModel(UnitProvisioning provisioning, ApplicationDbContext db) : PageModel
{
    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? WarningMessage { get; set; }

    public string? ErrorMessage { get; set; }

    protected ApplicationDbContext Db => db;

    protected Task<string?> UserIdAsync() => provisioning.GetUsableUserIdAsync(User);

    /// <summary>Shows a service failure as the error message and re-renders the page.</summary>
    protected async Task<IActionResult> FailAsync(string? message)
    {
        ErrorMessage = message;
        await LoadAsync();
        return Page();
    }

    /// <summary>Loads whatever the page shows; called on every render, including after a failed post.</summary>
    protected abstract Task LoadAsync();

    /// <summary>Email for each user id, for "performed by" columns. A removed account shows as such.</summary>
    protected async Task<Dictionary<string, string>> EmailsAsync(IEnumerable<string?> userIds)
    {
        var ids = userIds.Where(i => i is not null).Select(i => i!).Distinct().ToList();
        return await db.Users.Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Email ?? u.UserName ?? u.Id);
    }

    public static string Who(Dictionary<string, string> emails, string? userId) =>
        userId is null ? "-" : emails.TryGetValue(userId, out var email) ? email : "(removed account)";

    public static string When(DateTimeOffset at) => ProductionChecklistFormat.Utc(at);
}

public static class ProductionChecklistFormat
{
    public static string Utc(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd HH:mm") + " UTC";

    /// <summary>The value of a datetime-local input, in UTC.</summary>
    public static string Input(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm");

    /// <summary>Parses a datetime-local value as UTC. Null for blank; false for a value that isn't a date.</summary>
    public static bool TryParseInput(string? text, out DateTimeOffset? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
            return true;
        if (!DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
            return false;
        value = new DateTimeOffset(parsed, TimeSpan.Zero);
        return true;
    }
}
