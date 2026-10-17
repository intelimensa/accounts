using System.ComponentModel.DataAnnotations;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Intelimensa.Accounts.Publishing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Pages.Staff;

/// <summary>
/// Manual release publishing: create a release, upload one artifact per platform, then publish.
/// Only Published releases are visible on the participant Download page and to the update check.
/// The rules themselves live in <see cref="ReleasePublisher"/>, shared with the publisher API.
/// </summary>
[RequestSizeLimit(MaxArtifactBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = MaxArtifactBytes)]
public class ReleasesModel(ApplicationDbContext db, ReleasePublisher publisher) : PageModel
{
    // Kestrel/form defaults cap uploads around 30 MB, far below a desktop app installer.
    private const long MaxArtifactBytes = 1L * 1024 * 1024 * 1024;

    public List<Release> Releases { get; private set; } = [];

    public bool SignatureRequired => publisher.SignatureRequired;

    [BindProperty]
    public CreateInput Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        var version = Input.Version.Trim();
        if (await db.Releases.AnyAsync(r => r.Version == version))
            return await FailAsync($"Release {version} already exists.");

        var result = await publisher.CreateOrUpdateDraftAsync(version, Input.Notes, Input.MinimumVersion);
        if (!result.Ok) return await FailAsync(result.Message!);

        StatusMessage = $"Created draft release {result.Value!.Version}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUploadAsync(
        int releaseId, ReleasePlatform platform, IFormFile? file, string? signature, CancellationToken ct)
    {
        var release = await db.Releases.FirstOrDefaultAsync(r => r.Id == releaseId, ct);
        if (release is null) return NotFound();
        if (file is null || file.Length == 0)
            return await FailAsync("Choose a file to upload.");

        await using var content = file.OpenReadStream();
        var result = await publisher.UploadArtifactAsync(release.Version, platform, file.FileName, content, signature, ct);
        if (!result.Ok) return await FailAsync(result.Message!);

        StatusMessage = $"Uploaded {result.Value!.FileName} for {platform.DisplayName()} ({release.Version}).";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveArtifactAsync(int artifactId)
    {
        var result = await publisher.RemoveArtifactAsync(artifactId);
        if (result.Error == PublishError.NotFound) return NotFound();
        if (!result.Ok) return await FailAsync(result.Message!);

        StatusMessage = $"Removed {result.Value!.FileName}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSetStatusAsync(int releaseId, ReleaseStatus status)
    {
        var result = await publisher.SetStatusAsync(releaseId, status);
        if (result.Error == PublishError.NotFound) return NotFound();
        if (!result.Ok) return await FailAsync(result.Message!);

        StatusMessage = $"Release {result.Value!.Version} is now {status}.";
        return RedirectToPage();
    }

    private async Task<IActionResult> FailAsync(string message)
    {
        ErrorMessage = message;
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        var releases = await db.Releases.Include(r => r.Artifacts).ToListAsync();
        // SQLite's EF provider can't ORDER BY DateTimeOffset -- sort client-side.
        Releases = releases.OrderByDescending(r => r.CreatedAt).ToList();
    }

    public class CreateInput
    {
        [Required, StringLength(50)]
        public string Version { get; set; } = string.Empty;

        [StringLength(50), Display(Name = "Minimum version")]
        public string? MinimumVersion { get; set; }

        [StringLength(4000)]
        public string? Notes { get; set; }
    }
}
