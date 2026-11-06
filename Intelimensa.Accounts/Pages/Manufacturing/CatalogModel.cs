using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Manufacturing;
using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.Mvc;

namespace Intelimensa.Accounts.Pages.Manufacturing;

/// <summary>The parts and assemblies catalog: search, and issue a new part number.</summary>
public class CatalogModel(UnitProvisioning provisioning, CatalogService catalog, ApplicationDbContext db)
    : ProductionPageModel(provisioning, db)
{
    /// <summary>"part", "assembly" or blank for both.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Kind { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    /// <summary>Comma-separated tags to filter by: an item must carry all of them.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Tags { get; set; }

    /// <summary>Every tag in use, for the clickable list.</summary>
    public List<(string Tag, int Items)> KnownTags { get; private set; } = [];

    public List<Row> Rows { get; private set; } = [];

    public int Total { get; private set; }

    public record Row(CatalogItem Item, int Lines, int UsedBy, bool Superseded, IReadOnlyList<CatalogTag> Tags);

    public Task OnGetAsync() => LoadAsync();

    public async Task<IActionResult> OnPostCreateAsync(
        string? kind, string? partNo, string? name, string? description, string? reference, string? supersedes, string? tags)
    {
        if (await UserIdAsync() is not { } userId)
            return Forbid();
        var parsedKind = string.Equals(kind, "assembly", StringComparison.OrdinalIgnoreCase) ? CatalogKind.Assembly : CatalogKind.Part;
        var older = (supersedes ?? "").Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
        var result = await catalog.CreateAsync(userId, parsedKind, partNo, name, description, reference, older, tags);
        if (!result.IsOk)
            return await FailAsync(result.Message);
        StatusMessage = $"{result.Value!.PartNo} issued.";
        return RedirectToPage("/Manufacturing/CatalogItem", new { id = result.Value.Id });
    }

    protected override async Task LoadAsync()
    {
        CatalogKind? kind = Kind?.ToLowerInvariant() switch { "part" => CatalogKind.Part, "assembly" => CatalogKind.Assembly, _ => null };
        var graph = await catalog.LoadGraphAsync();
        Total = graph.Items.Count;
        var (wanted, tagError) = CatalogTags.ParseList(Tags);
        if (tagError is not null)
        {
            ErrorMessage = tagError;
            wanted = [];
        }
        KnownTags = await catalog.TagCountsAsync();
        var found = await catalog.SearchAsync(kind, Q, includeRetired: true, limit: 300, tags: wanted);
        var usedBy = graph.Items.Values.Where(i => i.IsAssembly)
            .SelectMany(a => graph.ChildrenOf(a.Id)).GroupBy(c => c.ChildId).ToDictionary(g => g.Key, g => g.Count());
        Rows = found.Select(i => new Row(i, graph.ChildrenOf(i.Id).Count, usedBy.GetValueOrDefault(i.Id), graph.IsSuperseded(i.Id), graph.TagsOf(i.Id))).ToList();
    }
}
