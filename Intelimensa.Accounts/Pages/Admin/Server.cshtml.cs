using Intelimensa.Accounts.ServerInfo;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Intelimensa.Accounts.Pages.Admin;

public class ServerModel(ServerUsageService serverUsage) : PageModel
{
    public ServerUsage Usage { get; private set; } = null!;

    public IReadOnlyList<(string Label, string Value)> System { get; private set; } = [];

    /// <summary>Bootstrap colour per bar segment, in the order of <see cref="ServerUsage.Items"/>, then "other".</summary>
    public static readonly string[] Colors = ["primary", "warning", "success", "info", "danger", "dark", "secondary"];

    public void OnGet()
    {
        Response.Headers.CacheControl = "no-store";
        Usage = serverUsage.Measure();
        System = serverUsage.Describe();
    }
}
