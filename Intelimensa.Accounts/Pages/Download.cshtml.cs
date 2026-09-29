using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Intelimensa.Accounts.Pages;

public class DownloadModel : PageModel
{
    public IReadOnlyList<PlatformDownload> Platforms { get; } = BuildPlatforms();

    public void OnGet()
    {
    }

    // Placeholder data -- no build/release pipeline exists yet, so there are no real artifacts to
    // link to. URLs are "#" until a release process produces actual downloads. Version listings
    // are intentionally omitted until a version actually ships.
    private static IReadOnlyList<PlatformDownload> BuildPlatforms() =>
    [
        new("Windows (x64)", "#"),
        new("macOS (Apple Silicon)", "#"),
        new("macOS (Intel)", "#"),
        new("Linux (x64)", "#", Enabled: false, Note: "Untested"),
    ];

    public record PlatformDownload(string Name, string Url, bool Enabled = true, string? Note = null);
}
