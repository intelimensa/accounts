using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Manufacturing;

public enum ChecklistStatus
{
    Ok,
    BadRequest,
    NotFound,
    Conflict,

    /// <summary>The unit is paired with a participant and the caller didn't confirm the change.</summary>
    NeedsPairedConfirmation,
}

public record ChecklistResult<T>(ChecklistStatus Status, T? Value, string? Message = null, IReadOnlyList<string>? Warnings = null)
{
    public bool IsOk => Status == ChecklistStatus.Ok;
}

/// <summary>What the caller wants a step to hold. Null fields are "not specified".</summary>
public record StepInput(
    DateTimeOffset? PerformedAt, string? Note, int? MountingTypeId, Guid? ElectrodeSetId);

/// <summary>
/// A unit's production checklist (docs/production-checklist.md): mark a step done, edit it, undo it. Any
/// step can be done in any order, on any unit that exists (held, reserved or voided included). Only the
/// <c>Manufacturer</c> role may call these (the pages authorize first and pass the user id in); a unit that
/// is paired with a participant needs an explicit confirmation. Every change appends a
/// <see cref="BciDeviceEvent"/> in the same save. Registration never looks at any of this.
/// </summary>
public class ProductionChecklist(ApplicationDbContext db, TimeProvider clock)
{
    public const int MaxNoteLength = 1000;

    private static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    /// <summary>The steps in the order they are shown.</summary>
    public static IReadOnlyList<ProductionStepType> Steps { get; } = Enum.GetValues<ProductionStepType>();

    public static string Title(ProductionStepType step) => step switch
    {
        ProductionStepType.ElectronicsFlashed => "Electronics flashed",
        ProductionStepType.CasingManufactured => "Casing manufactured",
        ProductionStepType.CasingAssembled => "Casing assembled",
        ProductionStepType.ElectrodesAssigned => "Electrodes assigned",
        ProductionStepType.MountingAssigned => "Mounting assigned",
        ProductionStepType.QcPassed => "QC passed",
        ProductionStepType.Packaged => "Packaged",
        _ => step.ToString(),
    };

    /// <summary>
    /// Step 1 as written by the server when a flash is confirmed (Quarry through the API, or the
    /// manual page). Does nothing if the step is already recorded: a rekey and re-confirm never moves it.
    /// The caller saves.
    /// </summary>
    public static void RecordFlash(ApplicationDbContext db, BciDevice unit, string userId, DateTimeOffset now, ProductionStepSource source)
    {
        var exists = db.ProductionSteps.Local.Any(s => s.BciDeviceId == unit.Id && s.Step == ProductionStepType.ElectronicsFlashed)
            || db.ProductionSteps.Any(s => s.BciDeviceId == unit.Id && s.Step == ProductionStepType.ElectronicsFlashed);
        if (exists)
            return;

        db.ProductionSteps.Add(new ProductionStep
        {
            BciDeviceId = unit.Id,
            Step = ProductionStepType.ElectronicsFlashed,
            PerformedAt = now,
            RecordedAt = now,
            PerformedByUserId = userId,
            Source = source,
        });
        db.BciDeviceEvents.Add(new BciDeviceEvent
        {
            BciDeviceId = unit.Id,
            Type = BciDeviceEventType.StepRecorded,
            Step = ProductionStepType.ElectronicsFlashed,
            OccurredAt = now,
            UserId = userId,
        });
    }

    public async Task<UnitChecklist?> GetAsync(string? serialNumber)
    {
        var unit = await db.BciDevices.FindBySerialAsync(serialNumber);
        if (unit is null)
            return null;

        var steps = await db.ProductionSteps
            .Include(s => s.MountingType).Include(s => s.ElectrodeSet)
            .Where(s => s.BciDeviceId == unit.Id).ToListAsync();
        var paired = await IsPairedAsync(unit.Id);
        return new UnitChecklist(unit, steps.OrderBy(s => s.Step).ToList(), paired);
    }

    /// <summary>The unit's checklist events, newest first. Sorted client-side: SQLite can't order a DateTimeOffset.</summary>
    public async Task<List<BciDeviceEvent>> HistoryAsync(Guid unitId) =>
        (await db.BciDeviceEvents.Where(e => e.BciDeviceId == unitId && e.Step != null).ToListAsync())
            .OrderByDescending(e => e.OccurredAt).ToList();

    public async Task<ChecklistResult<ProductionStep>> MarkDoneAsync(
        string userId, string? serialNumber, ProductionStepType step, StepInput input, bool confirmPaired)
    {
        var loaded = await LoadAsync(serialNumber, step, confirmPaired);
        if (loaded.Failure is { } failure)
            return Fail<ProductionStep>(failure);
        var unit = loaded.Unit!;

        if (loaded.Existing is not null)
            return Fail<ProductionStep>(ChecklistStatus.Conflict, $"{Title(step)} is already recorded for this unit; edit it instead.");

        var now = clock.GetUtcNow();
        var performedAt = input.PerformedAt ?? now;
        var valid = await ValidateAsync(step, input, existing: null);
        if (valid is not null)
            return Fail<ProductionStep>(ChecklistStatus.BadRequest, valid);

        var row = new ProductionStep
        {
            BciDeviceId = unit.Id,
            Step = step,
            PerformedAt = performedAt,
            RecordedAt = now,
            PerformedByUserId = userId,
            Source = ProductionStepSource.Manual,
            MountingTypeId = input.MountingTypeId,
            ElectrodeSetId = input.ElectrodeSetId,
            Note = Clean(input.Note),
        };
        db.ProductionSteps.Add(row);
        await FreezeSetAsync(input.ElectrodeSetId, now);
        AddEvent(unit, BciDeviceEventType.StepRecorded, step, userId, now, loaded.Paired, Clean(input.Note));
        await db.SaveChangesAsync();

        return new(ChecklistStatus.Ok, row, Warnings: await WarningsAsync(unit.Id, step, performedAt, now));
    }

    public async Task<ChecklistResult<ProductionStep>> EditAsync(
        string userId, string? serialNumber, ProductionStepType step, StepInput input, bool confirmPaired)
    {
        var loaded = await LoadAsync(serialNumber, step, confirmPaired);
        if (loaded.Failure is { } failure)
            return Fail<ProductionStep>(failure);
        var unit = loaded.Unit!;
        var row = loaded.Existing;
        if (row is null)
            return Fail<ProductionStep>(ChecklistStatus.Conflict, $"{Title(step)} isn't recorded for this unit; mark it done instead.");

        var valid = await ValidateAsync(step, input, row);
        if (valid is not null)
            return Fail<ProductionStep>(ChecklistStatus.BadRequest, valid);

        var now = clock.GetUtcNow();
        var changes = new List<string>();
        var performedAt = input.PerformedAt ?? row.PerformedAt;
        if (performedAt != row.PerformedAt)
        {
            changes.Add($"date {Format(row.PerformedAt)} to {Format(performedAt)}");
            row.PerformedAt = performedAt;
        }
        var note = Clean(input.Note);
        if (note != row.Note)
        {
            changes.Add("note changed");
            row.Note = note;
        }
        if (step == ProductionStepType.MountingAssigned && input.MountingTypeId != row.MountingTypeId)
        {
            changes.Add($"mounting {await MountingNameAsync(row.MountingTypeId)} to {await MountingNameAsync(input.MountingTypeId)}");
            row.MountingTypeId = input.MountingTypeId;
        }
        if (step == ProductionStepType.ElectrodesAssigned && input.ElectrodeSetId != row.ElectrodeSetId)
        {
            changes.Add($"electrode set {await SetNameAsync(row.ElectrodeSetId)} to {await SetNameAsync(input.ElectrodeSetId)}");
            row.ElectrodeSetId = input.ElectrodeSetId;
            await FreezeSetAsync(input.ElectrodeSetId, now);
        }

        if (changes.Count > 0)
        {
            AddEvent(unit, BciDeviceEventType.StepEdited, step, userId, now, loaded.Paired, string.Join("; ", changes));
            await db.SaveChangesAsync();
        }

        return new(ChecklistStatus.Ok, row, Warnings: await WarningsAsync(unit.Id, step, performedAt, now));
    }

    public async Task<ChecklistResult<ProductionStep>> UndoAsync(
        string userId, string? serialNumber, ProductionStepType step, string? reason, bool confirmPaired)
    {
        var loaded = await LoadAsync(serialNumber, step, confirmPaired);
        if (loaded.Failure is { } failure)
            return Fail<ProductionStep>(failure);
        var row = loaded.Existing;
        if (row is null)
            return Fail<ProductionStep>(ChecklistStatus.Conflict, $"{Title(step)} isn't recorded for this unit.");
        if (NoteError(reason) is { } error)
            return Fail<ProductionStep>(ChecklistStatus.BadRequest, error);

        db.ProductionSteps.Remove(row);
        AddEvent(loaded.Unit!, BciDeviceEventType.StepUndone, step, userId, clock.GetUtcNow(), loaded.Paired, Clean(reason));
        await db.SaveChangesAsync();
        return new(ChecklistStatus.Ok, row);
    }

    // ---- internals -------------------------------------------------------------------------------------------------

    private record Loaded(BciDevice? Unit, ProductionStep? Existing, bool Paired, ChecklistResult<object>? Failure);

    private async Task<Loaded> LoadAsync(string? serialNumber, ProductionStepType step, bool confirmPaired)
    {
        if (!Enum.IsDefined(step))
            return new(null, null, false, new(ChecklistStatus.BadRequest, null, "Unknown step."));

        var unit = await db.BciDevices.FindBySerialAsync(serialNumber);
        if (unit is null)
            return new(null, null, false, new(ChecklistStatus.NotFound, null, "Unknown serial number."));

        var paired = await IsPairedAsync(unit.Id);
        if (paired && !confirmPaired)
            return new(unit, null, true, new(ChecklistStatus.NeedsPairedConfirmation, null,
                "This unit is already paired with a participant. Confirm that you want to change its checklist."));

        var existing = await db.ProductionSteps.FirstOrDefaultAsync(s => s.BciDeviceId == unit.Id && s.Step == step);
        return new(unit, existing, paired, null);
    }

    private Task<bool> IsPairedAsync(Guid unitId) =>
        db.AccountDevices.AnyAsync(a => a.BciDeviceId == unitId && a.UnassignedAt == null);

    /// <summary>An error message, or null. Only the step that carries a value may be given one.</summary>
    private async Task<string?> ValidateAsync(ProductionStepType step, StepInput input, ProductionStep? existing)
    {
        if (NoteError(input.Note) is { } noteError)
            return noteError;
        if (input.MountingTypeId is not null && step != ProductionStepType.MountingAssigned)
            return "Only the mounting step takes a mounting type.";
        if (input.ElectrodeSetId is not null && step != ProductionStepType.ElectrodesAssigned)
            return "Only the electrodes step takes an electrode set.";

        if (input.MountingTypeId is { } mountingId)
        {
            var type = await db.MountingTypes.FindAsync(mountingId);
            if (type is null)
                return "Unknown mounting type.";
            if (type.RetiredAt is not null && existing?.MountingTypeId != mountingId)
                return "That mounting type is retired and can't be picked again.";
        }

        if (input.ElectrodeSetId is { } setId)
        {
            var set = await db.ElectrodeSets.FindAsync(setId);
            if (set is null)
                return "Unknown electrode set.";
            if (set.RetiredAt is not null && existing?.ElectrodeSetId != setId)
                return "That electrode set is retired and can't be picked again.";
        }

        return null;
    }

    private async Task FreezeSetAsync(Guid? setId, DateTimeOffset now)
    {
        if (setId is null)
            return;
        var set = await db.ElectrodeSets.FindAsync(setId.Value);
        if (set is { FrozenAt: null })
            set.FrozenAt = now;
    }

    /// <summary>Soft hints that never block: a date in the future, Packaged without QC.</summary>
    private async Task<IReadOnlyList<string>> WarningsAsync(Guid unitId, ProductionStepType step, DateTimeOffset performedAt, DateTimeOffset now)
    {
        var warnings = new List<string>();
        if (performedAt > now + FutureTolerance)
            warnings.Add($"{Title(step)} is dated in the future ({Format(performedAt)}).");
        if (step == ProductionStepType.Packaged
            && !await db.ProductionSteps.AnyAsync(s => s.BciDeviceId == unitId && s.Step == ProductionStepType.QcPassed))
            warnings.Add("QC not recorded.");
        return warnings;
    }

    private void AddEvent(BciDevice unit, BciDeviceEventType type, ProductionStepType step, string userId, DateTimeOffset now, bool paired, string? note) =>
        db.BciDeviceEvents.Add(new BciDeviceEvent
        {
            BciDeviceId = unit.Id,
            Type = type,
            Step = step,
            OccurredAt = now,
            UserId = userId,
            WasPaired = paired,
            Note = note,
        });

    private async Task<string> MountingNameAsync(int? id) =>
        id is null ? "(none)" : (await db.MountingTypes.FindAsync(id.Value))?.DisplayName ?? "(unknown)";

    private async Task<string> SetNameAsync(Guid? id) =>
        id is null ? "(none)" : (await db.ElectrodeSets.FindAsync(id.Value)) is { } s ? $"{s.Name} {s.Version}" : "(unknown)";

    internal static string Format(DateTimeOffset when) => when.UtcDateTime.ToString("yyyy-MM-dd HH:mm") + " UTC";

    private static string? Clean(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    /// <summary>Notes: at most <see cref="MaxNoteLength"/> characters, no control characters except line breaks.</summary>
    public static string? NoteError(string? note)
    {
        if (note is null)
            return null;
        if (note.Length > MaxNoteLength)
            return $"A note can be at most {MaxNoteLength} characters.";
        return note.Any(c => char.IsControl(c) && c is not ('\n' or '\r')) ? "A note can't contain control characters." : null;
    }

    private static ChecklistResult<T> Fail<T>(ChecklistStatus status, string message) => new(status, default, message);

    private static ChecklistResult<T> Fail<T>(ChecklistResult<object> failure) => new(failure.Status, default, failure.Message);
}

/// <summary>A unit with its recorded steps (ordered) and whether a participant is paired with it.</summary>
public record UnitChecklist(BciDevice Unit, IReadOnlyList<ProductionStep> Steps, bool Paired)
{
    public ProductionStep? Get(ProductionStepType step) => Steps.FirstOrDefault(s => s.Step == step);
}
