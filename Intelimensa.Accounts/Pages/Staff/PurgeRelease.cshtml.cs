using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Intelimensa.Accounts.Publishing;
using Intelimensa.Accounts.ServerInfo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Pages.Staff;

/// <summary>The confirmation step before a withdrawn release's installers are deleted for good.</summary>
public class PurgeReleaseModel(ApplicationDbContext db, ReleasePublisher publisher) : PageModel
{
    public Release? Release { get; private set; }

    public long TotalBytes => Release?.Artifacts.Sum(a => a.SizeBytes) ?? 0;

    [BindProperty]
    public string? ConfirmVersion { get; set; }

    public string? ErrorMessage { get; set; }

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(int releaseId)
    {
        return await LoadAsync(releaseId) ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostAsync(int releaseId)
    {
        if (!await LoadAsync(releaseId)) return NotFound();

        // Typing the version is the confirmation: it can't be clicked through by habit.
        if (!string.Equals(ConfirmVersion?.Trim(), Release!.Version, StringComparison.Ordinal))
        {
            ErrorMessage = $"Type {Release.Version} exactly to confirm.";
            return Page();
        }

        var result = await publisher.PurgeFilesAsync(releaseId);
        if (!result.Ok)
        {
            ErrorMessage = result.Message;
            return Page();
        }

        StatusMessage = $"Purged release {Release.Version}: {ByteSize.Format(result.Value)} freed.";
        return RedirectToPage("/Staff/Releases");
    }

    private async Task<bool> LoadAsync(int releaseId)
    {
        Release = await db.Releases.Include(r => r.Artifacts).FirstOrDefaultAsync(r => r.Id == releaseId);
        return Release is not null;
    }
}
