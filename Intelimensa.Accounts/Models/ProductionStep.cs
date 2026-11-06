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

    /// <summary>
    /// The catalog assembly the unit carries: set only on <see cref="ProductionStepType.ElectrodesAssigned"/> and
    /// <see cref="ProductionStepType.MountingAssigned"/>, and optional there (a shipped unit whose parts are unknown).
    /// </summary>
    public Guid? AssemblyId { get; set; }

    public CatalogItem? Assembly { get; set; }

    public string? Note { get; set; }
}
