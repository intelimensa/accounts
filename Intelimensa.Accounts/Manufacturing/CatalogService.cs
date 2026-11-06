using System.Text.RegularExpressions;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Manufacturing;

/// <summary>The part number: letters, digits, '-' and '_', at most 64, a string (never a number), unique ignoring case.</summary>
public static partial class PartNumber
{
    public const int MaxLength = 64;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex Valid();

    /// <summary>Trims, then checks the characters and length.</summary>
    public static bool TryNormalize(string? input, out string value)
    {
        value = input?.Trim() ?? string.Empty;
        return Valid().IsMatch(value);
    }

    public const string Rule = "A part number is 1 to 64 letters, digits, '-' or '_'.";
}

/// <summary>An item of an assembly's tree: how many of it the parent lists, how many in all (counts multiplied down the tree).</summary>
public record TreeNode(CatalogItem Item, int Count, long Total, IReadOnlyList<TreeNode> Children);

/// <summary>A part with how many of it the assembly needs in all: counts multiplied down the tree, and added across branches.</summary>
public record FlatLine(CatalogItem Part, long Total);

/// <summary>A unit that carries an item, through the assembly of step 4 or 5.</summary>
public record UnitUse(Guid UnitId, string SerialNumber, ProductionStepType Step, CatalogItem Assembly);

/// <summary>
/// The whole catalog graph, loaded in one go (hundreds of items): the items, the contents of each assembly, who lists
/// whom and who supersedes whom. The catalog is small, so tree walks, usage queries and cycle checks are done in memory.
/// </summary>
public class CatalogGraph(
    Dictionary<Guid, CatalogItem> items,
    Dictionary<Guid, List<CatalogChild>> children,
    List<CatalogSupersession> supersessions,
    Dictionary<Guid, List<CatalogTag>>? tags = null)
{
    public IReadOnlyDictionary<Guid, CatalogItem> Items => items;

    /// <summary>The tags of an item, sorted.</summary>
    public IReadOnlyList<CatalogTag> TagsOf(Guid id) =>
        tags is not null && tags.TryGetValue(id, out var list) ? list : [];

    public bool HasTag(Guid id, string tag) => TagsOf(id).Any(t => t.Tag == tag);

    public IReadOnlyList<CatalogChild> ChildrenOf(Guid assemblyId) =>
        children.TryGetValue(assemblyId, out var list) ? list : [];

    /// <summary>Assemblies that list <paramref name="id"/> directly.</summary>
    public IEnumerable<CatalogItem> ParentsOf(Guid id) =>
        children.Values.SelectMany(l => l).Where(c => c.ChildId == id).Select(c => items[c.AssemblyId]).DistinctBy(i => i.Id);

    /// <summary>The ids below <paramref name="id"/> at any depth, not including itself.</summary>
    public HashSet<Guid> Descendants(Guid id)
    {
        var seen = new HashSet<Guid>();
        var stack = new Stack<Guid>();
        stack.Push(id);
        while (stack.Count > 0)
            foreach (var child in ChildrenOf(stack.Pop()))
                if (seen.Add(child.ChildId))
                    stack.Push(child.ChildId);
        return seen;
    }

    /// <summary>Levels of the subtree: 1 for a part or an empty assembly, 2 for an assembly that holds only parts, and so on.</summary>
    public int Height(Guid id) => Height(id, []);

    private int Height(Guid id, HashSet<Guid> path)
    {
        if (!path.Add(id))
            return 0; // a cycle can't exist (it is refused on every edit); this only keeps a bad database from looping
        var below = ChildrenOf(id).Select(c => Height(c.ChildId, path)).DefaultIfEmpty(0).Max();
        path.Remove(id);
        return 1 + below;
    }

    public TreeNode Tree(Guid id, int count = 1, long multiplier = 1) =>
        new(items[id], count, multiplier * count,
            ChildrenOf(id).Select(c => Tree(c.ChildId, c.Count, multiplier * count)).ToList());

    public List<FlatLine> Flatten(Guid id)
    {
        var totals = new Dictionary<Guid, long>();
        var order = new List<Guid>();
        void Walk(TreeNode node)
        {
            if (!node.Item.IsAssembly)
            {
                if (!totals.ContainsKey(node.Item.Id)) order.Add(node.Item.Id);
                totals[node.Item.Id] = totals.GetValueOrDefault(node.Item.Id) + node.Total;
            }
            foreach (var child in node.Children) Walk(child);
        }
        Walk(Tree(id));
        return order.Select(i => new FlatLine(items[i], totals[i])).ToList();
    }

    /// <summary>Items that supersede <paramref name="id"/> directly.</summary>
    public IEnumerable<CatalogItem> SupersededBy(Guid id) =>
        supersessions.Where(s => s.OldId == id).Select(s => items[s.NewId]);

    /// <summary>Items that <paramref name="id"/> supersedes directly.</summary>
    public IEnumerable<CatalogItem> Supersedes(Guid id) =>
        supersessions.Where(s => s.NewId == id).Select(s => items[s.OldId]);

    /// <summary>The newest items that replace <paramref name="id"/>, following chains. Empty if nothing supersedes it.</summary>
    public List<CatalogItem> Newest(Guid id)
    {
        var result = new List<CatalogItem>();
        var seen = new HashSet<Guid> { id };
        var stack = new Stack<Guid>();
        stack.Push(id);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            var successors = SupersededBy(current).ToList();
            if (successors.Count == 0 && current != id)
                result.Add(items[current]);
            foreach (var next in successors)
                if (seen.Add(next.Id))
                    stack.Push(next.Id);
        }
        return result.OrderBy(i => i.PartNo, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public bool IsSuperseded(Guid id) => supersessions.Any(s => s.OldId == id);

    /// <summary>True if superseding would close a cycle: <paramref name="oldId"/> already (through others) supersedes <paramref name="newId"/>.</summary>
    public bool SupersessionWouldCycle(Guid newId, Guid oldId)
    {
        var seen = new HashSet<Guid>();
        var stack = new Stack<Guid>();
        stack.Push(oldId);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == newId)
                return true;
            if (seen.Add(current))
                foreach (var next in Supersedes(current))
                    stack.Push(next.Id);
        }
        return false;
    }
}

/// <summary>
/// The parts and assemblies catalog (docs/production-checklist.md, sections 6 and 7). The rules live here, used by every page
/// and by the XML import: part numbers are unique across both kinds; an assembly's contents are frozen at its first
/// reference (another assembly lists it, or a unit is assigned it); no cycles; nothing is deleted, only retired; name,
/// description and reference edits are kept in a history. Callers authorize first (Manufacturer) and pass the user id in.
/// </summary>
public class CatalogService(ApplicationDbContext db, TimeProvider clock)
{
    public const int MaxNameLength = 100;
    public const int MaxDescriptionLength = 1000;
    public const int MaxReferenceLength = 200;
    public const int MaxChildren = 500;
    public const int MaxCount = 9999;
    public const int MaxDepth = 20;

    // ---- reading ---------------------------------------------------------------------------------------------------

    public async Task<CatalogGraph> LoadGraphAsync()
    {
        var items = await db.CatalogItems.AsNoTracking().ToDictionaryAsync(i => i.Id);
        var children = (await db.CatalogChildren.AsNoTracking().ToListAsync())
            .GroupBy(c => c.AssemblyId).ToDictionary(g => g.Key, g => g.OrderBy(c => c.Position).ToList());
        var supersessions = await db.CatalogSupersessions.AsNoTracking().ToListAsync();
        var tags = (await db.CatalogTags.AsNoTracking().ToListAsync())
            .GroupBy(t => t.ItemId).ToDictionary(g => g.Key, g => g.OrderBy(t => t.Tag, StringComparer.Ordinal).ToList());
        return new CatalogGraph(items, children, supersessions, tags);
    }

    public async Task<CatalogItem?> FindAsync(string? partNo) =>
        PartNumber.TryNormalize(partNo, out var normalized)
            ? await db.CatalogItems.FirstOrDefaultAsync(i => i.PartNo == normalized)
            : null;

    public Task<CatalogItem?> GetAsync(Guid id) => db.CatalogItems.FirstOrDefaultAsync(i => i.Id == id);

    /// <summary>Items of a kind matching the text (part number, name or description), active first. Client-side: NOCASE and ordering.</summary>
    public async Task<List<CatalogItem>> SearchAsync(
        CatalogKind? kind, string? text, bool includeRetired = true, int limit = 500, IReadOnlyCollection<string>? tags = null)
    {
        var all = await db.CatalogItems.AsNoTracking().Where(i => kind == null || i.Kind == kind).ToListAsync();
        var needle = text?.Trim();
        HashSet<Guid>? tagged = null;
        if (tags is { Count: > 0 })
            tagged = (await db.CatalogTags.AsNoTracking().Where(t => tags.Contains(t.Tag)).ToListAsync())
                .GroupBy(t => t.ItemId).Where(g => g.Count() == tags.Count).Select(g => g.Key).ToHashSet(); // all of the tags
        return all
            .Where(i => tagged is null || tagged.Contains(i.Id))
            .Where(i => includeRetired || i.RetiredAt is null)
            .Where(i => string.IsNullOrEmpty(needle)
                || i.PartNo.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || i.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || (i.Description?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(i => i.RetiredAt != null).ThenBy(i => i.PartNo, StringComparer.OrdinalIgnoreCase)
            .Take(limit).ToList();
    }

    public async Task<List<CatalogChange>> HistoryAsync(Guid itemId) =>
        (await db.CatalogChanges.Where(c => c.ItemId == itemId).ToListAsync()).OrderByDescending(c => c.At).ToList();

    /// <summary>Units that carry <paramref name="itemId"/>: as the assembly of step 4 or 5 itself, or anywhere below it.</summary>
    public async Task<List<UnitUse>> UnitsCarryingAsync(Guid itemId, CatalogGraph? graph = null)
    {
        graph ??= await LoadGraphAsync();
        var steps = await db.ProductionSteps.AsNoTracking().Include(s => s.BciDevice)
            .Where(s => s.AssemblyId != null).ToListAsync();
        var reaches = new Dictionary<Guid, bool>();
        var uses = new List<UnitUse>();
        foreach (var step in steps)
        {
            var assemblyId = step.AssemblyId!.Value;
            if (!reaches.TryGetValue(assemblyId, out var carries))
                reaches[assemblyId] = carries = assemblyId == itemId || graph.Descendants(assemblyId).Contains(itemId);
            if (carries)
                uses.Add(new UnitUse(step.BciDeviceId, step.BciDevice!.SerialNumber, step.Step, graph.Items[assemblyId]));
        }
        return uses.OrderBy(u => u.SerialNumber, StringComparer.Ordinal).ThenBy(u => u.Step).ToList();
    }

    // ---- items -----------------------------------------------------------------------------------------------------

    /// <summary>Creates a part or an assembly (a new assembly is a draft). <paramref name="supersedes"/> are existing part numbers of the same kind.</summary>
    public async Task<ChecklistResult<CatalogItem>> CreateAsync(
        string userId, CatalogKind kind, string? partNo, string? name, string? description, string? reference,
        IEnumerable<string>? supersedes = null, string? tags = null)
    {
        if (!Enum.IsDefined(kind))
            return Fail<CatalogItem>(ChecklistStatus.BadRequest, "Unknown kind.");
        if (!PartNumber.TryNormalize(partNo, out var number))
            return Fail<CatalogItem>(ChecklistStatus.BadRequest, PartNumber.Rule);
        if (CheckFields(name, description, reference) is { } error)
            return Fail<CatalogItem>(ChecklistStatus.BadRequest, error);
        var (tagList, tagError) = CatalogTags.ParseList(tags);
        if (tagError is not null)
            return Fail<CatalogItem>(ChecklistStatus.BadRequest, tagError);
        if (await db.CatalogItems.AnyAsync(i => i.PartNo == number))
            return Fail<CatalogItem>(ChecklistStatus.Conflict, $"Part number {number} already exists.");

        var older = new List<CatalogItem>();
        foreach (var oldNumber in (supersedes ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var old = await FindAsync(oldNumber);
            if (old is null)
                return Fail<CatalogItem>(ChecklistStatus.BadRequest, $"Superseded part number {oldNumber} doesn't exist.");
            if (old.Kind != kind)
                return Fail<CatalogItem>(ChecklistStatus.BadRequest, $"{old.PartNo} is a {old.Kind.ToString().ToLowerInvariant()}; a {kind.ToString().ToLowerInvariant()} can only supersede its own kind.");
            older.Add(old);
        }

        var now = clock.GetUtcNow();
        var item = new CatalogItem
        {
            Kind = kind, PartNo = number, Name = name!.Trim(), Description = Clean(description), Reference = Clean(reference),
            CreatedAt = now, CreatedByUserId = userId, LastModifiedAt = now, LastModifiedByUserId = userId,
        };
        db.CatalogItems.Add(item);
        foreach (var tag in tagList)
            db.CatalogTags.Add(new CatalogTag { Item = item, Tag = tag });
        foreach (var old in older)
            db.CatalogSupersessions.Add(new CatalogSupersession { New = item, OldId = old.Id, CreatedAt = now, CreatedByUserId = userId });
        await db.SaveChangesAsync();
        return new(ChecklistStatus.Ok, item);
    }

    /// <summary>Name, description and reference only: the part number and (for an assembly) the contents are not edited here.</summary>
    public async Task<ChecklistResult<CatalogItem>> UpdateAsync(string userId, Guid id, string? name, string? description, string? reference)
    {
        var item = await db.CatalogItems.FindAsync(id);
        if (item is null)
            return Fail<CatalogItem>(ChecklistStatus.NotFound, "Unknown item.");
        if (CheckFields(name, description, reference) is { } error)
            return Fail<CatalogItem>(ChecklistStatus.BadRequest, error);

        if (ApplyFieldChanges(item, name!.Trim(), Clean(description), Clean(reference), userId))
            await db.SaveChangesAsync();
        return new(ChecklistStatus.Ok, item);
    }

    /// <summary>Applies the three editable fields to a tracked item, recording each change. The caller saves. True if anything changed.</summary>
    internal bool ApplyFieldChanges(CatalogItem item, string name, string? description, string? reference, string userId)
    {
        var now = clock.GetUtcNow();
        var changed = false;
        void Track(string field, string? old, string? value, Action set)
        {
            if (old == value) return;
            set();
            db.CatalogChanges.Add(new CatalogChange { ItemId = item.Id, At = now, ByUserId = userId, Field = field, OldValue = old, NewValue = value });
            changed = true;
        }
        Track("Name", item.Name, name, () => item.Name = name);
        Track("Description", item.Description, description, () => item.Description = description);
        Track("Reference", item.Reference, reference, () => item.Reference = reference);
        if (changed)
            Stamp(item, userId);
        return changed;
    }

    public async Task<ChecklistResult<CatalogItem>> RetireAsync(string userId, Guid id)
    {
        var item = await db.CatalogItems.FindAsync(id);
        if (item is null)
            return Fail<CatalogItem>(ChecklistStatus.NotFound, "Unknown item.");
        if (item.RetiredAt is null)
        {
            item.RetiredAt = clock.GetUtcNow();
            item.RetiredByUserId = userId;
            await db.SaveChangesAsync();
        }
        return new(ChecklistStatus.Ok, item);
    }

    // ---- tags ------------------------------------------------------------------------------------------------------

    /// <summary>Every tag in use with how many items carry it, most used first.</summary>
    public async Task<List<(string Tag, int Items)>> TagCountsAsync() =>
        (await db.CatalogTags.AsNoTracking().GroupBy(t => t.Tag).Select(g => new { Tag = g.Key, N = g.Count() }).ToListAsync())
            .OrderByDescending(t => t.N).ThenBy(t => t.Tag, StringComparer.Ordinal).Select(t => (t.Tag, t.N)).ToList();

    /// <summary>
    /// Sets an item's tags to exactly the comma-separated list (a blank list clears them). Adding is always allowed; removing is
    /// refused for a tag that is locked on this item (a unit's step relied on it).
    /// </summary>
    public async Task<ChecklistResult<CatalogItem>> SetTagsAsync(string userId, Guid itemId, string? tagList)
    {
        var item = await db.CatalogItems.Include(i => i.Tags).FirstOrDefaultAsync(i => i.Id == itemId);
        if (item is null)
            return Fail<CatalogItem>(ChecklistStatus.NotFound, "Unknown item.");
        var (wanted, error) = CatalogTags.ParseList(tagList);
        if (error is not null)
            return Fail<CatalogItem>(ChecklistStatus.BadRequest, error);

        if (!ApplyTags(item, wanted, userId, out var locked))
            return Fail<CatalogItem>(ChecklistStatus.Conflict,
                $"The tag '{locked}' is locked on {item.PartNo}: a unit was assigned it because of that tag. It can't be removed.");
        await db.SaveChangesAsync();
        return new(ChecklistStatus.Ok, item);
    }

    /// <summary>
    /// Makes a tracked item's tags equal <paramref name="wanted"/> and records the change. The caller saves. Returns false (and the
    /// first locked tag) without changing anything if a locked tag would be removed; true otherwise, changed or not.
    /// </summary>
    internal bool ApplyTags(CatalogItem item, IReadOnlyCollection<string> wanted, string userId, out string? lockedTag)
    {
        lockedTag = item.Tags.Where(t => t.FrozenAt is not null && !wanted.Contains(t.Tag)).Select(t => t.Tag).Order().FirstOrDefault();
        if (lockedTag is not null)
            return false;

        var before = CatalogTags.Format(item.Tags.Select(t => t.Tag));
        foreach (var tag in item.Tags.Where(t => !wanted.Contains(t.Tag)).ToList())
        {
            item.Tags.Remove(tag);
            db.CatalogTags.Remove(tag);
        }
        foreach (var tag in wanted.Where(w => item.Tags.All(t => t.Tag != w)))
            item.Tags.Add(new CatalogTag { ItemId = item.Id, Tag = tag });

        var after = CatalogTags.Format(wanted);
        if (before == after)
            return true;
        db.CatalogChanges.Add(new CatalogChange
        {
            ItemId = item.Id, At = clock.GetUtcNow(), ByUserId = userId, Field = "Tags",
            OldValue = before.Length == 0 ? null : before, NewValue = after.Length == 0 ? null : after,
        });
        Stamp(item, userId);
        return true;
    }

    /// <summary>
    /// Locks a tag on an item: a unit's step relied on it. The caller saves. A no-op if the item doesn't carry the tag or it is
    /// already locked.
    /// </summary>
    public async Task FreezeTagAsync(Guid itemId, string tag)
    {
        var row = await db.CatalogTags.FindAsync(itemId, tag);
        if (row is { FrozenAt: null })
            row.FrozenAt = clock.GetUtcNow();
    }

    // ---- an assembly's contents (drafts only) ----------------------------------------------------------------------

    /// <summary>
    /// Adds a child to a draft assembly. The child (part or assembly) must exist and not be retired; adding an assembly
    /// freezes it, because it is now referenced. Refused: a frozen or retired assembly, a repeat child, a cycle, too deep, too many.
    /// </summary>
    public async Task<ChecklistResult<CatalogChild>> AddChildAsync(string userId, Guid assemblyId, Guid childId, int count)
    {
        var (assembly, failure) = await LoadDraftAsync(assemblyId);
        if (failure is not null)
            return Fail<CatalogChild>(failure);
        var child = await db.CatalogItems.FindAsync(childId);
        if (child is null)
            return Fail<CatalogChild>(ChecklistStatus.NotFound, "Unknown part or assembly.");
        if (count < 1 || count > MaxCount)
            return Fail<CatalogChild>(ChecklistStatus.BadRequest, $"A count must be between 1 and {MaxCount}.");
        if (child.RetiredAt is not null)
            return Fail<CatalogChild>(ChecklistStatus.Conflict, $"{child.PartNo} is retired and can't be added.");

        var graph = await LoadGraphAsync();
        var lines = graph.ChildrenOf(assemblyId);
        if (lines.Any(c => c.ChildId == childId))
            return Fail<CatalogChild>(ChecklistStatus.Conflict, $"{child.PartNo} is already in this assembly; change its count instead.");
        if (lines.Count >= MaxChildren)
            return Fail<CatalogChild>(ChecklistStatus.BadRequest, $"An assembly can have at most {MaxChildren} lines.");
        if (childId == assemblyId || graph.Descendants(childId).Contains(assemblyId))
            return Fail<CatalogChild>(ChecklistStatus.Conflict, "That would make the assembly contain itself.");
        if (graph.Height(childId) + 1 > MaxDepth)
            return Fail<CatalogChild>(ChecklistStatus.BadRequest, $"Assemblies can nest at most {MaxDepth} levels.");

        var line = new CatalogChild
        {
            AssemblyId = assemblyId, ChildId = childId, Count = count,
            Position = lines.Count == 0 ? 1 : lines.Max(c => c.Position) + 1,
        };
        db.CatalogChildren.Add(line);
        if (child.IsAssembly)
            Freeze(child);
        Stamp(assembly!, userId);
        await db.SaveChangesAsync();
        return new(ChecklistStatus.Ok, line);
    }

    public async Task<ChecklistResult<CatalogChild>> SetChildCountAsync(string userId, Guid assemblyId, Guid childId, int count)
    {
        var (assembly, failure) = await LoadDraftAsync(assemblyId);
        if (failure is not null)
            return Fail<CatalogChild>(failure);
        if (count < 1 || count > MaxCount)
            return Fail<CatalogChild>(ChecklistStatus.BadRequest, $"A count must be between 1 and {MaxCount}.");
        var line = await db.CatalogChildren.FindAsync(assemblyId, childId);
        if (line is null)
            return Fail<CatalogChild>(ChecklistStatus.NotFound, "That item isn't in this assembly.");

        if (line.Count != count)
        {
            line.Count = count;
            Stamp(assembly!, userId);
            await db.SaveChangesAsync();
        }
        return new(ChecklistStatus.Ok, line);
    }

    /// <summary>Removes a line from a draft assembly. (A child that was frozen by being listed stays frozen.)</summary>
    public async Task<ChecklistResult<CatalogChild>> RemoveChildAsync(string userId, Guid assemblyId, Guid childId)
    {
        var (assembly, failure) = await LoadDraftAsync(assemblyId);
        if (failure is not null)
            return Fail<CatalogChild>(failure);
        var line = await db.CatalogChildren.FindAsync(assemblyId, childId);
        if (line is null)
            return Fail<CatalogChild>(ChecklistStatus.NotFound, "That item isn't in this assembly.");

        db.CatalogChildren.Remove(line);
        Stamp(assembly!, userId);
        await db.SaveChangesAsync();
        return new(ChecklistStatus.Ok, line);
    }

    /// <summary>Freezes an assembly (a unit is being assigned it). The caller saves. No-op for a part or an already frozen assembly.</summary>
    public async Task FreezeAsync(Guid assemblyId)
    {
        if (await db.CatalogItems.FindAsync(assemblyId) is { } item)
            Freeze(item);
    }

    /// <summary>
    /// A revision: a new item of the same kind and a new part number, with the source's name, description and reference,
    /// and (for an assembly) a draft copy of its contents, each child assembly being frozen by the new reference.
    /// The source may be frozen or retired. Optionally records that the new one supersedes the source.
    /// </summary>
    public async Task<ChecklistResult<CatalogItem>> CopyAsNewAsync(string userId, Guid sourceId, string? newPartNo, bool supersedesSource)
    {
        var source = await db.CatalogItems.FindAsync(sourceId);
        if (source is null)
            return Fail<CatalogItem>(ChecklistStatus.NotFound, "Unknown item.");
        if (!PartNumber.TryNormalize(newPartNo, out var number))
            return Fail<CatalogItem>(ChecklistStatus.BadRequest, PartNumber.Rule);
        if (await db.CatalogItems.AnyAsync(i => i.PartNo == number))
            return Fail<CatalogItem>(ChecklistStatus.Conflict, $"Part number {number} already exists.");

        var now = clock.GetUtcNow();
        var copy = new CatalogItem
        {
            Kind = source.Kind, PartNo = number, Name = source.Name, Description = source.Description, Reference = source.Reference,
            CreatedAt = now, CreatedByUserId = userId, LastModifiedAt = now, LastModifiedByUserId = userId,
        };
        db.CatalogItems.Add(copy);
        foreach (var tag in await db.CatalogTags.Where(t => t.ItemId == sourceId).Select(t => t.Tag).ToListAsync())
            db.CatalogTags.Add(new CatalogTag { Item = copy, Tag = tag }); // copied unlocked: nothing relies on them yet
        if (source.IsAssembly)
        {
            var lines = await db.CatalogChildren.Include(c => c.Child).Where(c => c.AssemblyId == sourceId).ToListAsync();
            foreach (var line in lines.OrderBy(l => l.Position))
            {
                db.CatalogChildren.Add(new CatalogChild { Assembly = copy, ChildId = line.ChildId, Count = line.Count, Position = line.Position });
                if (line.Child!.IsAssembly)
                    Freeze(line.Child);
            }
        }
        if (supersedesSource)
            db.CatalogSupersessions.Add(new CatalogSupersession { New = copy, OldId = sourceId, CreatedAt = now, CreatedByUserId = userId });
        await db.SaveChangesAsync();
        return new(ChecklistStatus.Ok, copy);
    }

    // ---- supersedes ------------------------------------------------------------------------------------------------

    public async Task<ChecklistResult<CatalogSupersession>> AddSupersessionAsync(string userId, Guid newId, Guid oldId)
    {
        var newer = await db.CatalogItems.FindAsync(newId);
        var older = await db.CatalogItems.FindAsync(oldId);
        if (newer is null || older is null)
            return Fail<CatalogSupersession>(ChecklistStatus.NotFound, "Unknown item.");
        if (newId == oldId)
            return Fail<CatalogSupersession>(ChecklistStatus.BadRequest, "An item can't supersede itself.");
        if (newer.Kind != older.Kind)
            return Fail<CatalogSupersession>(ChecklistStatus.BadRequest, "A part can only supersede a part, an assembly an assembly.");

        var existing = await db.CatalogSupersessions.FindAsync(newId, oldId);
        if (existing is not null)
            return new(ChecklistStatus.Ok, existing);
        if ((await LoadGraphAsync()).SupersessionWouldCycle(newId, oldId))
            return Fail<CatalogSupersession>(ChecklistStatus.Conflict, $"{older.PartNo} already supersedes {newer.PartNo} (through other items); that would be a cycle.");

        var link = new CatalogSupersession { NewId = newId, OldId = oldId, CreatedAt = clock.GetUtcNow(), CreatedByUserId = userId };
        db.CatalogSupersessions.Add(link);
        await db.SaveChangesAsync();
        return new(ChecklistStatus.Ok, link);
    }

    public async Task<ChecklistResult<CatalogSupersession>> RemoveSupersessionAsync(Guid newId, Guid oldId)
    {
        var link = await db.CatalogSupersessions.FindAsync(newId, oldId);
        if (link is null)
            return Fail<CatalogSupersession>(ChecklistStatus.NotFound, "No such link.");
        db.CatalogSupersessions.Remove(link);
        await db.SaveChangesAsync();
        return new(ChecklistStatus.Ok, link);
    }

    // ---- internals -------------------------------------------------------------------------------------------------

    private async Task<(CatalogItem? Assembly, ChecklistResult<object>? Failure)> LoadDraftAsync(Guid assemblyId)
    {
        var item = await db.CatalogItems.FindAsync(assemblyId);
        if (item is null)
            return (null, new(ChecklistStatus.NotFound, null, "Unknown assembly."));
        if (!item.IsAssembly)
            return (null, new(ChecklistStatus.BadRequest, null, "A part has no contents."));
        if (item.FrozenAt is not null)
            return (null, new(ChecklistStatus.Conflict, null,
                $"{item.PartNo} is frozen (it is in use) and its contents can't change. Copy it under a new part number to make a revision."));
        if (item.RetiredAt is not null)
            return (null, new(ChecklistStatus.Conflict, null, $"{item.PartNo} is retired."));
        return (item, null);
    }

    private void Freeze(CatalogItem item)
    {
        if (item.IsAssembly && item.FrozenAt is null)
            item.FrozenAt = clock.GetUtcNow();
    }

    private void Stamp(CatalogItem item, string userId)
    {
        item.LastModifiedAt = clock.GetUtcNow();
        item.LastModifiedByUserId = userId;
    }

    internal static string? CheckFields(string? name, string? description, string? reference) =>
        ProductionText.Check("Name", name, MaxNameLength, true)
        ?? ProductionText.Check("Description", description, MaxDescriptionLength, false)
        ?? ProductionText.Check("Reference", reference, MaxReferenceLength, false);

    internal static string? Clean(string? value) => ProductionText.Clean(value);

    private static ChecklistResult<T> Fail<T>(ChecklistStatus status, string message) => new(status, default, message);

    private static ChecklistResult<T> Fail<T>(ChecklistResult<object> failure) => new(failure.Status, default, failure.Message);
}
