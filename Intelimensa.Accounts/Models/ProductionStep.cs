namespace Intelimensa.Accounts.Models;

/// <summary>
/// The steps of a unit's production checklist, in the order they are shown (the order is not enforced:
/// see docs/production-checklist.md). Stored as integers; values are never reused or renumbered.
/// </summary>
public enum ProductionStepType
{
    ElectronicsFlashed = 1,
    CasingManufactured = 2,
    CasingAssembled = 3,
    ElectrodesAssigned = 4,
    MountingAssigned = 5,
    QcPassed = 6,
    Packaged = 7,
}

public enum ProductionStepSource
{
    /// <summary>A Manufacturer marked it on the web page.</summary>
    Manual = 0,

    /// <summary>Written by the server when Quarry (the manufacturing station API) confirmed the flash.</summary>
    Quarry = 1,

    /// <summary>Filled in by the migration from a unit's existing <c>ManufacturedAt</c>.</summary>
    Backfill = 2,
}

/// <summary>
/// One done step of one unit's production checklist. A step with no row is not done. Created when a
/// Manufacturer marks it (or by the server at a flash confirm), deleted by an undo; every change also
/// appends a <see cref="BciDeviceEvent"/>.
/// </summary>
public class ProductionStep
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid BciDeviceId { get; set; }

    public BciDevice? BciDevice { get; set; }

    public ProductionStepType Step { get; set; }

    /// <summary>When the step was done: pre-filled with now, editable by the Manufacturer.</summary>
    public DateTimeOffset PerformedAt { get; set; }

    /// <summary>When the server saw it. Never edited.</summary>
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The account that marked the step done. Later edits by others leave it alone (they are in the event history).</summary>
    public string? PerformedByUserId { get; set; }

    public ProductionStepSource Source { get; set; }

    /// <summary>Set only on <see cref="ProductionStepType.MountingAssigned"/>.</summary>
    public int? MountingTypeId { get; set; }

    public MountingType? MountingType { get; set; }

    /// <summary>Set only on <see cref="ProductionStepType.ElectrodesAssigned"/>.</summary>
    public Guid? ElectrodeSetId { get; set; }

    public ElectrodeSet? ElectrodeSet { get; set; }

    public string? Note { get; set; }
}

/// <summary>A mounting clip type a unit can carry. Manufacturers maintain the list; entries are only ever retired.</summary>
public class MountingType
{
    public int Id { get; set; }

    /// <summary>Lowercase slug, unique.</summary>
    public required string Key { get; set; }

    public required string DisplayName { get; set; }

    public DateTimeOffset? RetiredAt { get; set; }

    public string? RetiredByUserId { get; set; }
}

/// <summary>
/// The complete electrode complement of a unit: assemblies made of parts, with counts. Defined by
/// Manufacturers. Once assigned to any unit it is frozen (<see cref="FrozenAt"/>, never cleared): a change is
/// a new revision, copied from it. A set is never deleted, only retired.
/// </summary>
public class ElectrodeSet
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>A label the manufacturer types ("A", "1.0"). No meaning to the software; (Name, Version) is unique.</summary>
    public required string Version { get; set; }

    /// <summary>The set this one was copied from by "New revision".</summary>
    public Guid? PreviousId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? CreatedByUserId { get; set; }

    public DateTimeOffset LastModifiedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? LastModifiedByUserId { get; set; }

    /// <summary>Set when first assigned to a unit; from then on the contents can't be edited.</summary>
    public DateTimeOffset? FrozenAt { get; set; }

    public DateTimeOffset? RetiredAt { get; set; }

    public string? RetiredByUserId { get; set; }

    public List<ElectrodeAssembly> Assemblies { get; set; } = [];
}

public class ElectrodeAssembly
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ElectrodeSetId { get; set; }

    public ElectrodeSet? ElectrodeSet { get; set; }

    public int Position { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public required string Version { get; set; }

    public string? PartNumber { get; set; }

    public int Count { get; set; } = 1;

    public List<ElectrodePart> Parts { get; set; } = [];
}

public class ElectrodePart
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ElectrodeAssemblyId { get; set; }

    public ElectrodeAssembly? ElectrodeAssembly { get; set; }

    public int Position { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public required string Version { get; set; }

    public string? PartNumber { get; set; }

    public int Count { get; set; } = 1;
}
