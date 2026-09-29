using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Intelimensa.Accounts.Security;
using Intelimensa.Accounts.Storage;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Pages;

/// <summary>
/// Login-gated AxoSync downloads. Requires an authenticated, active, non-expired account (the
/// same rule as API login); only <see cref="ReleaseStatus.Published"/> releases are visible.
/// </summary>
public class DownloadModel(
    ApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    IReleaseStorage storage) : PageModel
{
    public bool AccountUsable { get; private set; }

    public IReadOnlyList<PlatformDownload> Platforms { get; private set; } = [];

    public async Task OnGetAsync()
    {
        AccountUsable = await IsAccountUsableAsync();
        if (!AccountUsable) return;

        var releases = (await PublishedReleasesAsync())
            .OrderByDescending(r => r.PublishedAt)
            .ToList();

        Platforms = Enum.GetValues<ReleasePlatform>()
            .Select(platform =>
            {
                var builds = releases
                    .Select(r => (Release: r, Artifact: r.Artifacts.FirstOrDefault(a => a.Platform == platform)))
                    .Where(x => x.Artifact is not null)
                    .Select(x => new Build(x.Release.Version, x.Release.PublishedAt, x.Artifact!.SizeBytes, x.Artifact.Sha256))
                    .ToList();
                return new PlatformDownload(platform, builds.FirstOrDefault(), builds.Skip(1).ToList());
            })
            .ToList();
    }

    public async Task<IActionResult> OnGetFileAsync(string version, ReleasePlatform platform)
    {
        if (!await IsAccountUsableAsync()) return Forbid();

        var artifact = await db.ReleaseArtifacts
            .Include(a => a.Release)
            .FirstOrDefaultAsync(a =>
                a.Release!.Version == version &&
                a.Release.Status == ReleaseStatus.Published &&
                a.Platform == platform);
        if (artifact is null) return NotFound();

        return new FileStreamResult(storage.OpenRead(artifact.StorageKey), "application/octet-stream")
        {
            FileDownloadName = artifact.FileName,
            EnableRangeProcessing = true,
        };
    }

    private async Task<bool> IsAccountUsableAsync()
    {
        var userId = userManager.GetUserId(User);
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.UserId == userId);
        return AccountPolicy.IsUsable(account);
    }

    private async Task<List<Release>> PublishedReleasesAsync() =>
        await db.Releases
            .Include(r => r.Artifacts)
            .Where(r => r.Status == ReleaseStatus.Published)
            .ToListAsync();

    public record Build(string Version, DateTimeOffset? PublishedAt, long SizeBytes, string Sha256);

    /// <summary><see cref="Latest"/> is null when no published release has a build for this platform.</summary>
    public record PlatformDownload(ReleasePlatform Platform, Build? Latest, IReadOnlyList<Build> Older);
}
