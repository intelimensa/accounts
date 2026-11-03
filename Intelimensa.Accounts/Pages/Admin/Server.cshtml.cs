using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Intelimensa.Accounts.ServerInfo;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Pages.Admin;

public class ServerModel(ServerUsageService serverUsage, ApplicationDbContext db) : PageModel
{
    public ServerUsage Usage { get; private set; } = null!;

    public IReadOnlyList<(string Label, string Value)> System { get; private set; } = [];

    /// <summary>Bootstrap colour per bar segment, in the order of <see cref="ServerUsage.Items"/>, then "other".</summary>
    public static readonly string[] Colors = ["primary", "warning", "success", "info", "danger", "dark", "secondary"];

    /// <summary>The release storage folders by version (largest first), with the release each belongs to.</summary>
    public List<ReleaseSize> ReleaseSizes { get; private set; } = [];

    public record ReleaseSize(string Version, long Bytes, ReleaseStatus? Status, bool Purged);

    public async Task OnGetAsync()
    {
        Response.Headers.CacheControl = "no-store";
        Usage = serverUsage.Measure();
        System = serverUsage.Describe();

        var releases = await db.Releases.ToDictionaryAsync(r => r.Version);
        ReleaseSizes = serverUsage.ReleaseFolderSizes()
            .Select(f => releases.TryGetValue(f.Key, out var r)
                ? new ReleaseSize(f.Key, f.Value, r.Status, r.FilesPurgedAt is not null)
                : new ReleaseSize(f.Key, f.Value, null, false))
            .OrderByDescending(r => r.Bytes)
            .ToList();
    }
}
