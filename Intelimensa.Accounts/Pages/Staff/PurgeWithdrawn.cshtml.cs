using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Intelimensa.Accounts.Publishing;
using Intelimensa.Accounts.ServerInfo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Pages.Staff;

/// <summary>The confirmation step before every withdrawn release's installers are deleted for good.</summary>
public class PurgeWithdrawnModel(ApplicationDbContext db, ReleasePublisher publisher) : PageModel
{
    public List<Release> Releases { get; private set; } = [];

    public long TotalBytes => Releases.Sum(r => r.Artifacts.Sum(a => a.SizeBytes));

    /// <summary>The phrase to type, which names how many releases are about to go.</summary>
    public string ConfirmPhrase => $"purge {Releases.Count} {(Releases.Count == 1 ? "release" : "releases")}";

    [BindProperty]
    public string? Confirm { get; set; }

    /// <summary>The releases the page showed. Only these are purged, even if more were withdrawn since.</summary>
    [BindProperty]
    public List<int> ReleaseIds { get; set; } = [];

    public string? ErrorMessage { get; set; }

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task OnGetAsync()
    {
        await LoadAsync(null);
        ReleaseIds = Releases.Select(r => r.Id).ToList();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadAsync(ReleaseIds);
        if (Releases.Count == 0)
        {
            ErrorMessage = "There is nothing left to purge.";
            return Page();
        }

        // Typing the phrase is the confirmation: it can't be clicked through by habit.
        if (!string.Equals(Confirm?.Trim(), ConfirmPhrase, StringComparison.Ordinal))
        {
            ErrorMessage = $"Type \"{ConfirmPhrase}\" exactly to confirm.";
            return Page();
        }

        var summary = await publisher.PurgeFilesAsync(Releases.Select(r => r.Id));
        StatusMessage = $"Purged {summary.Purged.Count} release(s): {ByteSize.Format(summary.BytesFreed)} freed."
            + (summary.Skipped.Count > 0 ? $" Skipped: {string.Join("; ", summary.Skipped)}" : "");
        return RedirectToPage("/Staff/Releases");
    }

    /// <summary>Withdrawn releases that still have files: all of them, or just the given ones.</summary>
    private async Task LoadAsync(List<int>? ids)
    {
        var query = db.Releases.Include(r => r.Artifacts)
            .Where(r => r.Status == ReleaseStatus.Withdrawn && r.Artifacts.Any());
        if (ids is not null) query = query.Where(r => ids.Contains(r.Id));
        Releases = (await query.ToListAsync()).OrderBy(r => r.CreatedAt).ToList();
    }
}
