using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Intelimensa.Accounts.Pages;

public class DownloadModel : PageModel
{
    public IReadOnlyList<PlatformDownloads> Platforms { get; } = BuildPlatforms();

    public void OnGet()
    {
    }

    // Placeholder data -- no build/release pipeline exists yet, so there are no real artifacts to
    // link to. URLs are "#" until a release process produces actual downloads.
    private static IReadOnlyList<PlatformDownloads> BuildPlatforms()
    {
        ReleaseVersion[] releases =
        [
            new("1.3.0", "2026-08-20", "#"),
            new("1.2.1", "2026-07-02", "#"),
            new("1.2.0", "2026-06-15", "#"),
            new("1.1.0", "2026-04-30", "#"),
        ];

        PlatformDownloads Make(string name) => new(name, releases[0], releases[1..]);

        return
        [
            Make("Windows (x64)"),
            Make("macOS (Apple Silicon)"),
            Make("macOS (Intel)"),
            Make("Linux (x64)"),
        ];
    }

    public record ReleaseVersion(string Version, string ReleasedOn, string Url);

    public record PlatformDownloads(string Name, ReleaseVersion Latest, IReadOnlyList<ReleaseVersion> OlderVersions);
}
