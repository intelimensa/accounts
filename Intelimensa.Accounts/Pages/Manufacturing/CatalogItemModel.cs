using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Manufacturing;
using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.Mvc;

namespace Intelimensa.Accounts.Pages.Manufacturing;

/// <summary>One part or assembly: its fields, its contents (editable while a draft), supersedes links, usage and history.</summary>
public class CatalogItemModel(UnitProvisioning provisioning, CatalogService catalog, CatalogXml xml, ApplicationDbContext db)
    : ProductionPageModel(provisioning, db)
{
    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    public CatalogItem? Item { get; private set; }

    public CatalogGraph Graph { get; private set; } = new([], [], []);

    public TreeNode? Tree { get; private set; }

    public List<FlatLine> Flat { get; private set; } = [];

    public List<CatalogItem> ParentAssemblies { get; private set; } = [];

    public List<CatalogItem> Supersedes { get; private set; } = [];

    public List<CatalogItem> SupersededBy { get; private set; } = [];

    public List<CatalogItem> Newest { get; private set; } = [];

    public List<UnitUse> Units { get; private set; } = [];

    public List<CatalogChange> History { get; private set; } = [];

    /// <summary>The item's tags (sorted); a locked one has a <see cref="CatalogTag.FrozenAt"/>.</summary>
    public IReadOnlyList<CatalogTag> Tags { get; private set; } = [];

    /// <summary>The tags in use anywhere, as suggestions.</summary>
    public List<(string Tag, int Items)> KnownTags { get; private set; } = [];

    public Dictionary<string, string> Emails { get; private set; } = [];

    /// <summary>Items that can be added as children (active, not this one).</summary>
    public List<CatalogItem> ChildOptions { get; private set; } = [];

    public bool IsDraft => Item is { Kind: CatalogKind.Assembly, FrozenAt: null, RetiredAt: null };

    public Task OnGetAsync() => LoadAsync();

    public async Task<IActionResult> OnGetExportAsync()
    {
        if (await UserIdAsync() is null)
            return Forbid();
        var item = await catalog.GetAsync(Id);
        if (item is not { Kind: CatalogKind.Assembly })
            return NotFound();
        return File(await xml.ExportAsync(Id), "application/xml", $"assembly-{item.PartNo}.xml");
    }

    public async Task<IActionResult> OnPostSaveAsync(string? name, string? description, string? reference) =>
        await RunAsync(userId => catalog.UpdateAsync(userId, Id, name, description, reference), "Saved.", _ => "top");

    public async Task<IActionResult> OnPostSaveTagsAsync(string? tags) =>
        await RunAsync(userId => catalog.SetTagsAsync(userId, Id, tags), "Tags saved.", _ => "tags");

    public async Task<IActionResult> OnPostRetireAsync() =>
        await RunAsync(userId => catalog.RetireAsync(userId, Id), "Retired.", _ => "top");

    public async Task<IActionResult> OnPostAddChildAsync(string? childPartNo, int count)
    {
        if (await UserIdAsync() is not { } userId)
            return Forbid();
        var child = await catalog.FindAsync(childPartNo);
        if (child is null)
            return await FailAsync($"'{childPartNo?.Trim()}' isn't a part number in the catalog.");
        var result = await catalog.AddChildAsync(userId, Id, child.Id, count);
        if (!result.IsOk)
            return await FailAsync(result.Message);
        StatusMessage = $"{child.PartNo} added.";
        return RedirectToPage(pageName: null, pageHandler: null, routeValues: new { id = Id }, fragment: $"c-{child.Id}");
    }

    public async Task<IActionResult> OnPostSetCountAsync(Guid childId, int count) =>
        await RunAsync(userId => catalog.SetChildCountAsync(userId, Id, childId, count), "Count saved.", _ => $"c-{childId}");

    public async Task<IActionResult> OnPostRemoveChildAsync(Guid childId) =>
        await RunAsync(userId => catalog.RemoveChildAsync(userId, Id, childId), "Removed.", _ => "contents");

    public async Task<IActionResult> OnPostSupersedesAsync(string? oldPartNo)
    {
        if (await UserIdAsync() is not { } userId)
            return Forbid();
        var old = await catalog.FindAsync(oldPartNo);
        if (old is null)
            return await FailAsync($"'{oldPartNo?.Trim()}' isn't a part number in the catalog.");
        var result = await catalog.AddSupersessionAsync(userId, Id, old.Id);
        if (!result.IsOk)
            return await FailAsync(result.Message);
        StatusMessage = $"Now supersedes {old.PartNo}.";
        return RedirectToPage(pageName: null, pageHandler: null, routeValues: new { id = Id }, fragment: "supersedes");
    }

    public async Task<IActionResult> OnPostUnsupersedeAsync(Guid oldId)
    {
        if (await UserIdAsync() is null)
            return Forbid();
        var result = await catalog.RemoveSupersessionAsync(Id, oldId);
        if (!result.IsOk)
            return await FailAsync(result.Message);
        StatusMessage = "Link removed.";
        return RedirectToPage(pageName: null, pageHandler: null, routeValues: new { id = Id }, fragment: "supersedes");
    }

    public async Task<IActionResult> OnPostCopyAsync(string? newPartNo, bool supersedesSource)
    {
        if (await UserIdAsync() is not { } userId)
            return Forbid();
        var result = await catalog.CopyAsNewAsync(userId, Id, newPartNo, supersedesSource);
        if (!result.IsOk)
            return await FailAsync(result.Message);
        StatusMessage = $"Copied as {result.Value!.PartNo}. It is a draft: change what differs.";
        return RedirectToPage(new { id = result.Value.Id });
    }

    private async Task<IActionResult> RunAsync<T>(Func<string, Task<ChecklistResult<T>>> action, string done, Func<T, string> anchor)
    {
        if (await UserIdAsync() is not { } userId)
            return Forbid();
        var result = await action(userId);
        if (!result.IsOk)
            return await FailAsync(result.Message);
        StatusMessage = done;
        return RedirectToPage(pageName: null, pageHandler: null, routeValues: new { id = Id }, fragment: anchor(result.Value!));
    }

    protected override async Task LoadAsync()
    {
        Graph = await catalog.LoadGraphAsync();
        if (!Graph.Items.TryGetValue(Id, out var item))
            return;
        Item = await catalog.GetAsync(Id); // tracked copy is not needed; the graph's is read-only

        ParentAssemblies = Graph.ParentsOf(Id).OrderBy(p => p.PartNo, StringComparer.OrdinalIgnoreCase).ToList();
        Supersedes = Graph.Supersedes(Id).OrderBy(i => i.PartNo, StringComparer.OrdinalIgnoreCase).ToList();
        SupersededBy = Graph.SupersededBy(Id).OrderBy(i => i.PartNo, StringComparer.OrdinalIgnoreCase).ToList();
        Newest = Graph.Newest(Id);
        Tags = Graph.TagsOf(Id);
        KnownTags = await catalog.TagCountsAsync();
        Units = await catalog.UnitsCarryingAsync(Id, Graph);
        History = await catalog.HistoryAsync(Id);
        Emails = await EmailsAsync(History.Select(h => h.ByUserId)
            .Concat([item.CreatedByUserId, item.LastModifiedByUserId, item.RetiredByUserId]));

        if (item.IsAssembly)
        {
            Tree = Graph.Tree(Id);
            Flat = Graph.Flatten(Id);
        }
        if (IsDraft)
            ChildOptions = Graph.Items.Values.Where(i => i.Id != Id && i.RetiredAt is null)
                .OrderBy(i => i.PartNo, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
