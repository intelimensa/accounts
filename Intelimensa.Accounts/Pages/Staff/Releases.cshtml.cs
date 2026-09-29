using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Intelimensa.Accounts.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Pages.Staff;

/// <summary>
/// Manual release publishing: create a release, upload one artifact per platform, then publish.
/// Only Published releases are visible on the participant Download page.
/// </summary>
[RequestSizeLimit(MaxArtifactBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = MaxArtifactBytes)]
public partial class ReleasesModel(ApplicationDbContext db, IReleaseStorage storage) : PageModel
{
    // Kestrel/form defaults cap uploads around 30 MB, far below a desktop app installer.
    private const long MaxArtifactBytes = 1L * 1024 * 1024 * 1024;

    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z]+(\.[0-9A-Za-z]+)*)?$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._ -]*$")]
    private static partial Regex FileNamePattern();

    public List<Release> Releases { get; private set; } = [];

    [BindProperty]
    public CreateInput Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (!ModelState.IsValid || !VersionPattern().IsMatch(Input.Version.Trim()))
        {
            if (ModelState.IsValid) ErrorMessage = "Version must look like 1.3.0 or 1.4.0-beta.1.";
            await LoadAsync();
            return Page();
        }

        var version = Input.Version.Trim();
        if (await db.Releases.AnyAsync(r => r.Version == version))
        {
            ErrorMessage = $"Release {version} already exists.";
            await LoadAsync();
            return Page();
        }

        db.Releases.Add(new Release
        {
            Version = version,
            Notes = string.IsNullOrWhiteSpace(Input.Notes) ? null : Input.Notes.Trim(),
        });
        await db.SaveChangesAsync();

        StatusMessage = $"Created draft release {version}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUploadAsync(
        int releaseId, ReleasePlatform platform, IFormFile? file, CancellationToken ct)
    {
        var release = await db.Releases.Include(r => r.Artifacts).FirstOrDefaultAsync(r => r.Id == releaseId, ct);
        if (release is null) return NotFound();

        if (release.Status == ReleaseStatus.Withdrawn)
            return await FailAsync("Withdrawn releases can't be modified.");
        if (!Enum.IsDefined(platform))
            return await FailAsync("Unknown platform.");
        if (file is null || file.Length == 0)
            return await FailAsync("Choose a file to upload.");

        // The name becomes both a storage path segment and the browser's saved filename.
        var fileName = Path.GetFileName(file.FileName);
        if (!FileNamePattern().IsMatch(fileName) || fileName.Length > 200 || fileName.Contains(".."))
            return await FailAsync("File name may only contain letters, digits, spaces, '.', '_' and '-'.");

        var stored = await storage.SaveAsync(release.Version, platform, fileName, file.OpenReadStream(), ct);

        // One artifact per (release, platform): uploading again replaces the previous one.
        var existing = release.Artifacts.FirstOrDefault(a => a.Platform == platform);
        if (existing is not null)
        {
            if (existing.StorageKey != stored.StorageKey) storage.Delete(existing.StorageKey);
            existing.FileName = fileName;
            existing.SizeBytes = stored.SizeBytes;
            existing.Sha256 = stored.Sha256;
            existing.StorageKey = stored.StorageKey;
            existing.UploadedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            db.ReleaseArtifacts.Add(new ReleaseArtifact
            {
                ReleaseId = release.Id,
                Platform = platform,
                FileName = fileName,
                SizeBytes = stored.SizeBytes,
                Sha256 = stored.Sha256,
                StorageKey = stored.StorageKey,
            });
        }
        await db.SaveChangesAsync(ct);

        StatusMessage = $"Uploaded {fileName} for {platform.DisplayName()} ({release.Version}).";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveArtifactAsync(int artifactId)
    {
        var artifact = await db.ReleaseArtifacts.Include(a => a.Release).FirstOrDefaultAsync(a => a.Id == artifactId);
        if (artifact is null) return NotFound();

        // Only drafts: removing a file from a published/withdrawn release would silently break
        // links and the audit trail of what participants could download.
        if (artifact.Release!.Status != ReleaseStatus.Draft)
            return await FailAsync("Artifacts can only be removed from draft releases.");

        storage.Delete(artifact.StorageKey);
        db.ReleaseArtifacts.Remove(artifact);
        await db.SaveChangesAsync();

        StatusMessage = $"Removed {artifact.FileName}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSetStatusAsync(int releaseId, ReleaseStatus status)
    {
        var release = await db.Releases.Include(r => r.Artifacts).FirstOrDefaultAsync(r => r.Id == releaseId);
        if (release is null) return NotFound();

        if (status == ReleaseStatus.Draft && release.PublishedAt is not null)
            return await FailAsync("A release that has been published can be withdrawn, not returned to draft.");
        if (status == ReleaseStatus.Published && release.Artifacts.Count == 0)
            return await FailAsync("Upload at least one artifact before publishing.");
        if (!Enum.IsDefined(status))
            return await FailAsync("Unknown status.");

        release.Status = status;
        if (status == ReleaseStatus.Published) release.PublishedAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        StatusMessage = $"Release {release.Version} is now {status}.";
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

        [StringLength(4000)]
        public string? Notes { get; set; }
    }
}
