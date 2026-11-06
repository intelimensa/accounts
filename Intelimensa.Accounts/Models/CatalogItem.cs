namespace Intelimensa.Accounts.Models;

public enum CatalogKind
{
    /// <summary>Indivisible: a single-body printed piece, a screw, a cable.</summary>
    Part = 1,

    /// <summary>Contains one or more children, each a part or another assembly, with counts.</summary>
    Assembly = 2,
}

/// <summary>
/// A part or an assembly of the manufacturing catalog (docs/production-checklist.md, sections 6 and 7). One table for both
/// kinds, so a part number is unique across parts and assemblies by construction. A part number never changes
/// meaning: a revision gets a new part number and says which one it supersedes (<see cref="CatalogSupersession"/>). An
/// assembly's contents are frozen at its first reference; name, description and reference stay editable (their edits are
/// kept in <see cref="CatalogChange"/>). Nothing is deleted, only retired.
/// </summary>
public class CatalogItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public CatalogKind Kind { get; set; }

    /// <summary>The part number: a string of letters, digits, '-' and '_', unique case-insensitively. Never parsed.</summary>
    public required string PartNo { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Free text, not interpreted: for example the frozen CAD file or revision this number stands for.</summary>
    public string? Reference { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? CreatedByUserId { get; set; }

    /// <summary>Of the item's own fields and, for a draft assembly, its children.</summary>
    public DateTimeOffset LastModifiedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? LastModifiedByUserId { get; set; }

    /// <summary>
    /// Assemblies only. Set at the first reference (another assembly lists it, or a unit is assigned it) and never
    /// cleared; from then on its children can't change. Null = still a draft.
    /// </summary>
    public DateTimeOffset? FrozenAt { get; set; }

    public DateTimeOffset? RetiredAt { get; set; }

    public string? RetiredByUserId { get; set; }

    public bool IsAssembly => Kind == CatalogKind.Assembly;

    public List<CatalogChild> Children { get; set; } = [];

    public List<CatalogTag> Tags { get; set; } = [];
}

/// <summary>One line of an assembly: a child (part or assembly) and how many of it. One line per child; Count covers repeats.</summary>
public class CatalogChild
{
    public Guid AssemblyId { get; set; }

    public CatalogItem? Assembly { get; set; }

    public Guid ChildId { get; set; }

    public CatalogItem? Child { get; set; }

    /// <summary>Display order: what was added or imported first comes first.</summary>
    public int Position { get; set; }

    /// <summary>1 to 9999.</summary>
    public int Count { get; set; } = 1;
}

/// <summary>"<see cref="NewId"/> supersedes <see cref="OldId"/>": recorded, never enforced; used to warn about superseded items.</summary>
public class CatalogSupersession
{
    public Guid NewId { get; set; }

    public CatalogItem? New { get; set; }

    public Guid OldId { get; set; }

    public CatalogItem? Old { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? CreatedByUserId { get; set; }
}

/// <summary>One edit of an item's name, description or reference: who, when, and the old and new value.</summary>
public class CatalogChange
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ItemId { get; set; }

    public CatalogItem? Item { get; set; }

    public DateTimeOffset At { get; set; }

    public string? ByUserId { get; set; }

    /// <summary>"Name", "Description" or "Reference".</summary>
    public required string Field { get; set; }

    public string? OldValue { get; set; }

    public string? NewValue { get; set; }
}

/// <summary>
/// A tag on a part or an assembly (lowercase letters, digits, '-' and '_'), used to filter the catalog and to say which
/// assemblies may be assigned to a unit's step (for example only assemblies tagged "electrode-set" for the electrodes).
/// Tags are freely editable, except one that a unit's step has relied on: that one is locked on the item
/// (<see cref="FrozenAt"/>, never cleared) so what a shipped unit was assigned stays true.
/// </summary>
public class CatalogTag
{
    public Guid ItemId { get; set; }

    public CatalogItem? Item { get; set; }

    public required string Tag { get; set; }

    /// <summary>Set when a unit's step first relied on this tag; the tag can't be removed from the item after that.</summary>
    public DateTimeOffset? FrozenAt { get; set; }
}
