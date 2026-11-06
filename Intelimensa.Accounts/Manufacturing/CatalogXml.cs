using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Manufacturing;

public record ImportIssue(int Line, string Message);

public record ImportReport(
    bool Applied, int Created, int Updated, int Unchanged, int Retired, int LinksAdded, IReadOnlyList<ImportIssue> Issues)
{
    public bool Ok => Issues.Count == 0;
}

/// <summary>
/// The catalog as XML (docs/production-checklist.md, section 7). Import is upsert by part number and never deletes; the
/// whole file is validated first and applied in one save or not at all, and every problem is reported with its line number.
/// Export writes the whole catalog or one assembly with everything below it.
/// </summary>
public class CatalogXml(ApplicationDbContext db, CatalogService catalog, TimeProvider clock)
{
    public const int FormatVersion = 1;
    public const long MaxBytes = 5 * 1024 * 1024;
    public const int MaxElements = 20_000;
    public const int MaxIssues = 200;

    private static readonly string[] DefinitionAttributes = ["PartNo", "Name", "Description", "Reference", "Supersedes", "Tags", "Retired"];
    private static readonly string[] ReferenceAttributes = ["PartNo", "Count"];

    private record Def(CatalogKind Kind, string PartNo, string Name, string? Description, string? Reference,
        List<string> Supersedes, bool Retire, int Line, List<Ref> Children, List<string>? Tags);

    private record Ref(CatalogKind Kind, string PartNo, int Count, int Line);

    // ---- import ----------------------------------------------------------------------------------------------------

    /// <summary>Validates the file and, unless <paramref name="validateOnly"/>, applies it. Nothing is applied if there is any issue.</summary>
    public async Task<ImportReport> ImportAsync(string userId, Stream xml, bool validateOnly)
    {
        var issues = new List<ImportIssue>();
        var defs = Parse(xml, issues);
        if (issues.Count == 0)
            await ValidateAsync(defs, issues);

        if (issues.Count > 0)
            return new(false, 0, 0, 0, 0, 0, issues.Take(MaxIssues).ToList());
        return await ApplyAsync(userId, defs, apply: !validateOnly);
    }

    private static List<Def> Parse(Stream xml, List<ImportIssue> issues)
    {
        XDocument doc;
        try
        {
            if (xml.CanSeek && xml.Length > MaxBytes)
            {
                issues.Add(new(0, $"The file is larger than {MaxBytes / (1024 * 1024)} MiB."));
                return [];
            }
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,   // no DTDs: no entity expansion tricks
                XmlResolver = null,                      // and nothing is ever fetched
                MaxCharactersInDocument = MaxBytes,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };
            using var reader = XmlReader.Create(xml, settings);
            doc = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException e)
        {
            issues.Add(new(e.LineNumber, $"Not well-formed XML: {e.Message}"));
            return [];
        }

        var root = doc.Root!;
        if (doc.DescendantNodes().OfType<XElement>().Count() > MaxElements)
        {
            issues.Add(new(0, $"The file has more than {MaxElements} elements."));
            return [];
        }
        if (root.Name != "Catalog")
        {
            issues.Add(new(Line(root), "The root element must be <Catalog>."));
            return [];
        }
        if (root.Attribute("Format")?.Value != FormatVersion.ToString(CultureInfo.InvariantCulture))
            issues.Add(new(Line(root), $"<Catalog> needs Format=\"{FormatVersion}\"."));
        Unknown(root, ["Format"], issues);

        var defs = new List<Def>();
        foreach (var section in root.Elements())
        {
            CatalogKind? kind = section.Name.LocalName switch { "Parts" => CatalogKind.Part, "Assemblies" => CatalogKind.Assembly, _ => null };
            if (kind is null)
            {
                issues.Add(new(Line(section), $"Unknown element <{section.Name.LocalName}> in <Catalog> (expected <Parts> or <Assemblies>)."));
                continue;
            }
            Unknown(section, [], issues);
            var itemName = kind == CatalogKind.Part ? "Part" : "Assembly";
            foreach (var element in section.Elements())
            {
                if (element.Name.LocalName != itemName)
                {
                    issues.Add(new(Line(element), $"Unknown element <{element.Name.LocalName}> in <{section.Name.LocalName}> (expected <{itemName}>)."));
                    continue;
                }
                if (ParseDefinition(element, kind.Value, issues) is { } def)
                    defs.Add(def);
            }
        }
        return defs;
    }

    private static Def? ParseDefinition(XElement element, CatalogKind kind, List<ImportIssue> issues)
    {
        var line = Line(element);
        var before = issues.Count;
        Unknown(element, DefinitionAttributes, issues);

        var partNo = element.Attribute("PartNo")?.Value ?? "";
        if (!PartNumber.TryNormalize(partNo, out var number))
            issues.Add(new(line, $"'{partNo}' is not a valid part number. {PartNumber.Rule}"));
        var name = element.Attribute("Name")?.Value;
        if (CatalogService.CheckFields(name, element.Attribute("Description")?.Value, element.Attribute("Reference")?.Value) is { } fieldError)
            issues.Add(new(line, $"{number}: {fieldError}"));

        var retire = false;
        if (element.Attribute("Retired") is { } retired)
        {
            if (retired.Value == "true") retire = true;
            else if (retired.Value != "false") issues.Add(new(line, $"{number}: Retired must be true or false."));
        }

        var supersedes = new List<string>();
        foreach (var token in (element.Attribute("Supersedes")?.Value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!PartNumber.TryNormalize(token, out var old))
                issues.Add(new(line, $"{number}: '{token}' in Supersedes is not a valid part number."));
            else
                supersedes.Add(old);
        }

        // Tags: absent leaves an existing item's tags alone; present (even empty) sets them exactly.
        List<string>? tags = null;
        if (element.Attribute("Tags") is { } tagsAttribute)
        {
            var (parsed, tagError) = CatalogTags.ParseList(tagsAttribute.Value);
            if (tagError is not null)
                issues.Add(new(line, $"{number}: {tagError}"));
            else
                tags = parsed;
        }

        var children = new List<Ref>();
        foreach (var child in element.Elements())
        {
            var childLine = Line(child);
            if (kind == CatalogKind.Part)
            {
                issues.Add(new(childLine, $"{number}: a part has no contents (unexpected <{child.Name.LocalName}>)."));
                continue;
            }
            CatalogKind? childKind = child.Name.LocalName switch { "Part" => CatalogKind.Part, "Assembly" => CatalogKind.Assembly, _ => null };
            if (childKind is null)
            {
                issues.Add(new(childLine, $"Unknown element <{child.Name.LocalName}> in <Assembly> (expected <Part> or <Assembly>)."));
                continue;
            }
            Unknown(child, ReferenceAttributes, issues);
            if (child.HasElements)
                issues.Add(new(childLine, "A reference has no contents; define the item in <Parts> or <Assemblies>."));
            if (!PartNumber.TryNormalize(child.Attribute("PartNo")?.Value, out var childNo))
                issues.Add(new(childLine, $"{number}: '{child.Attribute("PartNo")?.Value}' is not a valid part number."));
            var count = 1;
            if (child.Attribute("Count") is { } countAttribute
                && (!int.TryParse(countAttribute.Value, NumberStyles.None, CultureInfo.InvariantCulture, out count)
                    || count < 1 || count > CatalogService.MaxCount))
                issues.Add(new(childLine, $"{number}: Count must be a whole number from 1 to {CatalogService.MaxCount}."));
            children.Add(new Ref(childKind.Value, childNo, count, childLine));
        }

        return issues.Count > before
            ? null
            : new Def(kind, number, name!.Trim(), CatalogService.Clean(element.Attribute("Description")?.Value),
                CatalogService.Clean(element.Attribute("Reference")?.Value), supersedes, retire, line, children, tags);
    }

    private static void Unknown(XElement element, string[] allowed, List<ImportIssue> issues)
    {
        foreach (var attribute in element.Attributes())
            if (!allowed.Contains(attribute.Name.LocalName) || attribute.Name.Namespace != XNamespace.None)
                issues.Add(new(Line(element), $"Unknown attribute '{attribute.Name.LocalName}' on <{element.Name.LocalName}>."));
    }

    private static int Line(XObject node) => node is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 0;

    private static string KindName(CatalogKind kind) => kind.ToString().ToLowerInvariant();

    /// <summary>The checks that need the catalog: kinds, references, frozen contents, cycles, depth, supersedes.</summary>
    private async Task ValidateAsync(List<Def> defs, List<ImportIssue> issues)
    {
        var byNumber = new Dictionary<string, Def>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in defs)
            if (!byNumber.TryAdd(def.PartNo, def))
                issues.Add(new(def.Line, $"Part number {def.PartNo} is defined twice (first at line {byNumber[def.PartNo].Line})."));

        var graph = await catalog.LoadGraphAsync();
        var existing = graph.Items.Values.ToDictionary(i => i.PartNo, StringComparer.OrdinalIgnoreCase);

        CatalogKind? KindOf(string partNo) =>
            byNumber.TryGetValue(partNo, out var d) ? d.Kind : existing.TryGetValue(partNo, out var i) ? i.Kind : null;

        foreach (var def in defs.Where(d => byNumber[d.PartNo] == d))
        {
            existing.TryGetValue(def.PartNo, out var current);
            if (current is not null && current.Kind != def.Kind)
                issues.Add(new(def.Line, $"{def.PartNo} is a {KindName(current.Kind)} in the catalog and can't be redefined as a {KindName(def.Kind)}."));

            if (current is not null && def.Tags is not null)
                foreach (var locked in graph.TagsOf(current.Id).Where(t => t.FrozenAt is not null && !def.Tags.Contains(t.Tag)))
                    issues.Add(new(def.Line, $"The tag '{locked.Tag}' is locked on {def.PartNo} (a unit was assigned it because of that tag) and can't be removed."));

            foreach (var old in def.Supersedes)
            {
                if (string.Equals(old, def.PartNo, StringComparison.OrdinalIgnoreCase))
                    issues.Add(new(def.Line, $"{def.PartNo} can't supersede itself."));
                else if (KindOf(old) is not { } oldKind)
                    issues.Add(new(def.Line, $"{def.PartNo} supersedes {old}, which isn't in the file or the catalog."));
                else if (oldKind != def.Kind)
                    issues.Add(new(def.Line, $"{def.PartNo} supersedes {old}, which is not the same kind."));
            }

            if (def.Children.Count > CatalogService.MaxChildren)
                issues.Add(new(def.Line, $"{def.PartNo} has more than {CatalogService.MaxChildren} lines."));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in def.Children)
            {
                if (!seen.Add(child.PartNo))
                    issues.Add(new(child.Line, $"{def.PartNo} lists {child.PartNo} twice; use Count."));
                var childKind = KindOf(child.PartNo);
                if (childKind is null)
                    issues.Add(new(child.Line, $"{def.PartNo} lists {child.PartNo}, which isn't in the file or the catalog."));
                else if (childKind != child.Kind)
                    issues.Add(new(child.Line, $"{child.PartNo} is a {KindName(childKind.Value)}; list it with <{childKind}>."));
            }

            if (def.Kind != CatalogKind.Assembly || current is null || current.Kind != def.Kind)
                continue;

            var lines = graph.ChildrenOf(current.Id);
            var same = lines.Count == def.Children.Count
                && lines.All(l => def.Children.Any(c =>
                    string.Equals(c.PartNo, graph.Items[l.ChildId].PartNo, StringComparison.OrdinalIgnoreCase) && c.Count == l.Count));
            if (!same && current.FrozenAt is not null)
                issues.Add(new(def.Line, $"{def.PartNo} is frozen (it is in use) and its contents differ from the catalog's. Define the revision under a new part number."));
            else if (!same && current.RetiredAt is not null)
                issues.Add(new(def.Line, $"{def.PartNo} is retired; its contents can't change."));
            else if (!same)
                foreach (var child in def.Children)
                    if (existing.TryGetValue(child.PartNo, out var item) && item.RetiredAt is not null
                        && !lines.Any(l => l.ChildId == item.Id))
                        issues.Add(new(child.Line, $"{child.PartNo} is retired and can't be added to {def.PartNo}."));
        }

        // A new assembly can't list retired children either.
        foreach (var def in defs.Where(d => d.Kind == CatalogKind.Assembly && !existing.ContainsKey(d.PartNo)))
            foreach (var child in def.Children)
                if (existing.TryGetValue(child.PartNo, out var item) && item.RetiredAt is not null)
                    issues.Add(new(child.Line, $"{child.PartNo} is retired and can't be added to {def.PartNo}."));

        if (issues.Count > 0)
            return;

        // Cycles and depth, over the catalog as it will be after the import.
        var merged = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in graph.Items.Values.Where(i => i.IsAssembly))
            merged[item.PartNo] = graph.ChildrenOf(item.Id).Select(c => graph.Items[c.ChildId].PartNo).ToList();
        foreach (var def in defs.Where(d => d.Kind == CatalogKind.Assembly))
            if (!existing.TryGetValue(def.PartNo, out var current) || current.FrozenAt is null)
                merged[def.PartNo] = def.Children.Select(c => c.PartNo).ToList();

        foreach (var def in defs.Where(d => d.Kind == CatalogKind.Assembly))
        {
            var height = HeightOrCycle(def.PartNo, merged, [], out var cycle);
            if (cycle)
                issues.Add(new(def.Line, $"{def.PartNo} would contain itself (a cycle)."));
            else if (height > CatalogService.MaxDepth)
                issues.Add(new(def.Line, $"{def.PartNo} nests more than {CatalogService.MaxDepth} levels."));
        }

        // Supersedes cycles: the existing links plus the file's.
        var links = graph.Items.Values.SelectMany(i => graph.Supersedes(i.Id).Select(o => (i.PartNo, o.PartNo))).ToList();
        foreach (var def in defs)
            foreach (var old in def.Supersedes)
                links.Add((def.PartNo, old));
        var successors = links.GroupBy(l => l.Item1, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(l => l.Item2).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var def in defs.Where(d => d.Supersedes.Count > 0))
            if (Reaches(def.PartNo, def.PartNo, successors))
                issues.Add(new(def.Line, $"The Supersedes links through {def.PartNo} form a cycle."));
    }

    private static int HeightOrCycle(string partNo, Dictionary<string, List<string>> graph, HashSet<string> path, out bool cycle)
    {
        cycle = false;
        if (!graph.TryGetValue(partNo, out var children))
            return 1;
        if (!path.Add(partNo))
        {
            cycle = true;
            return 0;
        }
        var below = 0;
        foreach (var child in children)
        {
            below = Math.Max(below, HeightOrCycle(child, graph, path, out var childCycle));
            if (childCycle)
            {
                cycle = true;
                break;
            }
        }
        path.Remove(partNo);
        return 1 + below;
    }

    private static bool Reaches(string from, string target, Dictionary<string, List<string>> successors)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>(successors.GetValueOrDefault(from) ?? []);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
                return true;
            if (seen.Add(current))
                foreach (var next in successors.GetValueOrDefault(current) ?? [])
                    stack.Push(next);
        }
        return false;
    }

    /// <summary>Applies the (valid) definitions in one save; with <paramref name="apply"/> false it only counts what would happen.</summary>
    private async Task<ImportReport> ApplyAsync(string userId, List<Def> defs, bool apply)
    {
        var now = clock.GetUtcNow();
        var items = await db.CatalogItems.Include(i => i.Children).Include(i => i.Tags)
            .ToDictionaryAsync(i => i.PartNo, StringComparer.OrdinalIgnoreCase);
        var linkKeys = (await db.CatalogSupersessions.Select(l => new { l.NewId, l.OldId }).ToListAsync())
            .Select(l => (l.NewId, l.OldId)).ToHashSet();
        int created = 0, retired = 0, linksAdded = 0;
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var createdNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. New items, and the fields of existing ones.
        foreach (var def in defs)
        {
            if (!items.TryGetValue(def.PartNo, out var item))
            {
                item = new CatalogItem
                {
                    Kind = def.Kind, PartNo = def.PartNo, Name = def.Name, Description = def.Description, Reference = def.Reference,
                    CreatedAt = now, CreatedByUserId = userId, LastModifiedAt = now, LastModifiedByUserId = userId,
                };
                items[def.PartNo] = item;
                if (apply)
                {
                    db.CatalogItems.Add(item);
                    foreach (var tag in def.Tags ?? [])
                        item.Tags.Add(new CatalogTag { Tag = tag });
                }
                createdNumbers.Add(def.PartNo);
                created++;
            }
            else
            {
                var fieldsChanged = apply
                    ? catalog.ApplyFieldChanges(item, def.Name, def.Description, def.Reference, userId)
                    : item.Name != def.Name || item.Description != def.Description || item.Reference != def.Reference;
                var tagsChanged = def.Tags is not null
                    && CatalogTags.Format(item.Tags.Select(t => t.Tag)) != CatalogTags.Format(def.Tags);
                if (tagsChanged && apply)
                    catalog.ApplyTags(item, def.Tags!, userId, out _); // validated: no locked tag is removed
                if (fieldsChanged || tagsChanged)
                    touched.Add(def.PartNo);
            }
        }

        // 2. The contents of assemblies that aren't frozen; what they list becomes frozen.
        foreach (var def in defs.Where(d => d.Kind == CatalogKind.Assembly))
        {
            var item = items[def.PartNo];
            if (item.FrozenAt is not null)
                continue; // validated to be identical

            var position = 0;
            var wanted = def.Children.Select(c => (Item: items[c.PartNo], c.Count, Position: ++position)).ToList();
            var changed = false;
            foreach (var line in item.Children.Where(l => wanted.All(w => w.Item.Id != l.ChildId)).ToList())
            {
                if (apply) db.CatalogChildren.Remove(line);
                changed = true;
            }
            foreach (var w in wanted)
            {
                var line = item.Children.FirstOrDefault(l => l.ChildId == w.Item.Id);
                if (line is null)
                {
                    if (apply) db.CatalogChildren.Add(new CatalogChild { Assembly = item, Child = w.Item, Count = w.Count, Position = w.Position });
                    changed = true;
                }
                else if (line.Count != w.Count || line.Position != w.Position)
                {
                    line.Count = w.Count;
                    line.Position = w.Position;
                    changed = true;
                }
                if (apply && w.Item.IsAssembly && w.Item.FrozenAt is null)
                    w.Item.FrozenAt = now;
            }
            if (changed && !createdNumbers.Contains(def.PartNo))
            {
                touched.Add(def.PartNo);
                if (apply)
                {
                    item.LastModifiedAt = now;
                    item.LastModifiedByUserId = userId;
                }
            }
        }

        // 3. Supersedes links (added, never removed) and retiring (never undone).
        foreach (var def in defs)
        {
            var item = items[def.PartNo];
            foreach (var old in def.Supersedes)
            {
                var older = items[old];
                // A brand-new item has no id yet in a dry run; its links are new by definition.
                if (!createdNumbers.Contains(def.PartNo) && linkKeys.Contains((item.Id, older.Id)))
                    continue;
                linksAdded++;
                if (!createdNumbers.Contains(def.PartNo))
                    touched.Add(def.PartNo);
                if (apply)
                    db.CatalogSupersessions.Add(new CatalogSupersession { New = item, Old = older, CreatedAt = now, CreatedByUserId = userId });
            }
            if (def.Retire && item.RetiredAt is null)
            {
                retired++;
                if (!createdNumbers.Contains(def.PartNo))
                    touched.Add(def.PartNo);
                if (apply)
                {
                    item.RetiredAt = now;
                    item.RetiredByUserId = userId;
                }
            }
        }

        var updated = touched.Count;
        var unchanged = defs.Count - created - updated;
        if (apply)
            await db.SaveChangesAsync();
        return new(apply, created, updated, unchanged, retired, linksAdded, []);
    }

    // ---- export ----------------------------------------------------------------------------------------------------

    /// <summary>The whole catalog, or (with an id) that assembly and everything below it. Parts first, then assemblies, children before parents.</summary>
    public async Task<byte[]> ExportAsync(Guid? assemblyId = null)
    {
        var graph = await catalog.LoadGraphAsync();
        IEnumerable<CatalogItem> selected = graph.Items.Values;
        if (assemblyId is { } root)
        {
            var include = graph.Descendants(root);
            include.Add(root);
            selected = selected.Where(i => include.Contains(i.Id));
        }
        var chosen = selected.ToList();
        var ids = chosen.Select(i => i.Id).ToHashSet();

        var parts = chosen.Where(i => !i.IsAssembly).OrderBy(i => i.PartNo, StringComparer.OrdinalIgnoreCase);
        var assemblies = new List<CatalogItem>();
        var placed = new HashSet<Guid>();
        void Place(CatalogItem item)
        {
            if (!placed.Add(item.Id)) return;
            foreach (var child in graph.ChildrenOf(item.Id).Select(c => graph.Items[c.ChildId]).Where(c => c.IsAssembly))
                Place(child);
            assemblies.Add(item);
        }
        foreach (var assembly in chosen.Where(i => i.IsAssembly).OrderBy(i => i.PartNo, StringComparer.OrdinalIgnoreCase))
            Place(assembly);

        XElement Definition(CatalogItem item)
        {
            var element = new XElement(item.IsAssembly ? "Assembly" : "Part",
                new XAttribute("PartNo", item.PartNo), new XAttribute("Name", item.Name));
            if (item.Description is not null) element.Add(new XAttribute("Description", item.Description));
            if (item.Reference is not null) element.Add(new XAttribute("Reference", item.Reference));
            var supersedes = graph.Supersedes(item.Id).Where(o => ids.Contains(o.Id)).Select(o => o.PartNo)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            if (supersedes.Count > 0) element.Add(new XAttribute("Supersedes", string.Join(" ", supersedes)));
            var itemTags = graph.TagsOf(item.Id);
            if (itemTags.Count > 0) element.Add(new XAttribute("Tags", CatalogTags.Format(itemTags.Select(t => t.Tag))));
            if (item.RetiredAt is not null) element.Add(new XAttribute("Retired", "true"));
            if (item.IsAssembly)
                foreach (var line in graph.ChildrenOf(item.Id))
                {
                    var child = graph.Items[line.ChildId];
                    var reference = new XElement(child.IsAssembly ? "Assembly" : "Part", new XAttribute("PartNo", child.PartNo));
                    if (line.Count != 1) reference.Add(new XAttribute("Count", line.Count));
                    element.Add(reference);
                }
            return element;
        }

        var document = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement("Catalog", new XAttribute("Format", FormatVersion),
                new XElement("Parts", parts.Select(Definition)),
                new XElement("Assemblies", assemblies.Select(Definition))));

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) }))
            document.Save(writer);
        return stream.ToArray();
    }
}
