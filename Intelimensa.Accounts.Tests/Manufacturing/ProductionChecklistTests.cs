using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Manufacturing;
using Intelimensa.Accounts.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;


namespace Intelimensa.Accounts.Tests.Manufacturing;

public class ProductionChecklistTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ApplicationDbContext _db;
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
    private readonly ProductionChecklist _checklist;
    private readonly CatalogService _catalog;
    private readonly BciDevice _unit;

    public ProductionChecklistTests()
    {
        _connection.Open();
        _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser { Id = "m", UserName = "m" });
        _unit = NewUnit("MSV2G0000011", BciDeviceStatus.Reserved);
        _catalog = new CatalogService(_db, _clock);
        _checklist = new ProductionChecklist(_db, _clock, _catalog, Microsoft.Extensions.Options.Options.Create(new ManufacturingOptions()));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private BciDevice NewUnit(string serial, BciDeviceStatus status)
    {
        var unit = new BciDevice { SerialNumber = serial, DeviceType = "msv2", Status = status };
        _db.BciDevices.Add(unit);
        _db.SaveChanges();
        return unit;
    }

    private static StepInput Input(DateTimeOffset? at = null, string? note = null, Guid? assembly = null) =>
        new(at, note, assembly);

    /// <summary>An assembly with one part in it, tagged for both assembly steps: assignable to a unit.</summary>
    private async Task<CatalogItem> NewAssemblyAsync(string partNo, string tags = "electrode-set,mounting-clip")
    {
        var part = (await _catalog.CreateAsync("m", CatalogKind.Part, partNo + "-P", "Part " + partNo, null, null)).Value!;
        var assembly = (await _catalog.CreateAsync("m", CatalogKind.Assembly, partNo, "Assembly " + partNo, null, null, tags: tags)).Value!;
        await _catalog.AddChildAsync("m", assembly.Id, part.Id, 1);
        return assembly;
    }

    private Task<ChecklistResult<ProductionStep>> Mark(ProductionStepType step, StepInput? input = null, bool confirm = false, string? serial = null) =>
        _checklist.MarkDoneAsync("m", serial ?? _unit.SerialNumber, step, input ?? Input(), confirm);

    private void Pair(BciDevice unit)
    {
        _db.Users.Add(new ApplicationUser { Id = "p", UserName = "p" });
        var account = new Account { UserId = "p" };
        _db.Accounts.Add(account);
        _db.SaveChanges();
        _db.AccountDevices.Add(new AccountDevice { AccountId = account.Id, BciDeviceId = unit.Id });
        _db.SaveChanges();
    }

    // ---- marking ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Any_step_can_be_marked_in_any_order_on_an_unflashed_unit()
    {
        // Electrodes first, flash never: the unit is only reserved.
        var result = await Mark(ProductionStepType.ElectrodesAssigned);
        Assert.True(result.IsOk);
        Assert.True((await Mark(ProductionStepType.Packaged)).IsOk);

        var list = await _checklist.GetAsync(_unit.SerialNumber);
        Assert.Equal([ProductionStepType.ElectrodesAssigned, ProductionStepType.Packaged], list!.Steps.Select(s => s.Step));
    }

    [Fact]
    public async Task Marking_records_who_when_and_an_event()
    {
        var at = new DateTimeOffset(2026, 11, 1, 9, 30, 0, TimeSpan.Zero);
        var row = (await Mark(ProductionStepType.CasingManufactured, Input(at, "  first batch "))).Value!;

        Assert.Equal(at, row.PerformedAt);
        Assert.Equal(_clock.GetUtcNow(), row.RecordedAt);
        Assert.Equal("m", row.PerformedByUserId);
        Assert.Equal(ProductionStepSource.Manual, row.Source);
        Assert.Equal("first batch", row.Note);

        var e = Assert.Single(await _checklist.HistoryAsync(_unit.Id));
        Assert.Equal(BciDeviceEventType.StepRecorded, e.Type);
        Assert.Equal(ProductionStepType.CasingManufactured, e.Step);
        Assert.False(e.WasPaired);
    }

    [Fact]
    public async Task The_date_defaults_to_now()
    {
        var row = (await Mark(ProductionStepType.CasingAssembled)).Value!;
        Assert.Equal(_clock.GetUtcNow(), row.PerformedAt);
    }

    [Fact]
    public async Task Marking_twice_is_refused()
    {
        await Mark(ProductionStepType.QcPassed);
        var again = await Mark(ProductionStepType.QcPassed);
        Assert.Equal(ChecklistStatus.Conflict, again.Status);
    }

    [Fact]
    public async Task Unknown_serial_and_unknown_step_are_refused()
    {
        Assert.Equal(ChecklistStatus.NotFound, (await Mark(ProductionStepType.QcPassed, serial: "NOPE")).Status);
        Assert.Equal(ChecklistStatus.BadRequest, (await Mark((ProductionStepType)99)).Status);
    }

    [Fact]
    public async Task A_voided_unit_can_have_steps_recorded()
    {
        var voided = NewUnit("MSV2G0000029", BciDeviceStatus.Voided);
        Assert.True((await Mark(ProductionStepType.CasingManufactured, serial: voided.SerialNumber)).IsOk);
    }

    [Fact]
    public async Task Steps_do_not_touch_the_registration_status()
    {
        await Mark(ProductionStepType.Packaged);
        await Mark(ProductionStepType.QcPassed);
        Assert.Equal(BciDeviceStatus.Reserved, (await _db.BciDevices.FindAsync(_unit.Id))!.Status);
    }

    // ---- hints -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Packaged_without_qc_warns_but_saves()
    {
        var result = await Mark(ProductionStepType.Packaged);
        Assert.True(result.IsOk);
        Assert.Contains("QC not recorded.", result.Warnings!);
    }

    [Fact]
    public async Task Packaged_after_qc_does_not_warn()
    {
        await Mark(ProductionStepType.QcPassed);
        Assert.Empty((await Mark(ProductionStepType.Packaged)).Warnings!);
    }

    [Fact]
    public async Task A_future_date_warns_but_saves_and_an_earlier_date_does_not()
    {
        var future = await Mark(ProductionStepType.CasingManufactured, Input(_clock.GetUtcNow().AddDays(2)));
        Assert.True(future.IsOk);
        Assert.Single(future.Warnings!);

        // Electrodes dated before the flash is normal: no warning.
        var past = await Mark(ProductionStepType.ElectrodesAssigned, Input(_clock.GetUtcNow().AddDays(-9)));
        Assert.Empty(past.Warnings!);
    }

    // ---- values ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Only_steps_four_and_five_take_an_assembly()
    {
        var assembly = await NewAssemblyAsync("A1");
        Assert.Equal(ChecklistStatus.BadRequest, (await Mark(ProductionStepType.QcPassed, Input(assembly: assembly.Id))).Status);
        Assert.True((await Mark(ProductionStepType.ElectrodesAssigned, Input(assembly: assembly.Id))).IsOk);
        Assert.True((await Mark(ProductionStepType.MountingAssigned, Input(assembly: assembly.Id))).IsOk);
        Assert.Equal(ChecklistStatus.BadRequest,
            (await Mark(ProductionStepType.QcPassed, Input(assembly: Guid.NewGuid()))).Status);
    }

    [Fact]
    public async Task A_part_an_unknown_id_an_empty_or_a_retired_assembly_cannot_be_assigned()
    {
        var part = (await _catalog.CreateAsync("m", CatalogKind.Part, "P1", "Part", null, null)).Value!;
        var empty = (await _catalog.CreateAsync("m", CatalogKind.Assembly, "E1", "Empty", null, null, tags: "electrode-set")).Value!;
        var retired = await NewAssemblyAsync("R1");
        await _catalog.RetireAsync("m", retired.Id);

        Assert.Equal(ChecklistStatus.BadRequest, (await Mark(ProductionStepType.ElectrodesAssigned, Input(assembly: part.Id))).Status);
        Assert.Equal(ChecklistStatus.BadRequest, (await Mark(ProductionStepType.ElectrodesAssigned, Input(assembly: empty.Id))).Status);
        Assert.Equal(ChecklistStatus.BadRequest, (await Mark(ProductionStepType.ElectrodesAssigned, Input(assembly: retired.Id))).Status);
        Assert.Equal(ChecklistStatus.BadRequest, (await Mark(ProductionStepType.ElectrodesAssigned, Input(assembly: Guid.NewGuid()))).Status);
        // With no assembly at all the step can still be done (a shipped unit whose parts are unknown).
        Assert.True((await Mark(ProductionStepType.ElectrodesAssigned)).IsOk);
    }

    [Fact]
    public async Task A_unit_that_already_carries_an_assembly_can_keep_it_after_it_is_retired()
    {
        var assembly = await NewAssemblyAsync("A1");
        await Mark(ProductionStepType.MountingAssigned, Input(assembly: assembly.Id));
        await _catalog.RetireAsync("m", assembly.Id);

        var edit = await _checklist.EditAsync("m", _unit.SerialNumber, ProductionStepType.MountingAssigned,
            Input(note: "checked", assembly: assembly.Id), false);
        Assert.True(edit.IsOk);

        var other = NewUnit("MSV2G0000037", BciDeviceStatus.Reserved);
        Assert.Equal(ChecklistStatus.BadRequest,
            (await Mark(ProductionStepType.MountingAssigned, Input(assembly: assembly.Id), serial: other.SerialNumber)).Status);
    }

    [Fact]
    public async Task Assigning_an_assembly_freezes_it_for_good()
    {
        var assembly = await NewAssemblyAsync("A1");
        Assert.Null((await _db.CatalogItems.FindAsync(assembly.Id))!.FrozenAt);

        await Mark(ProductionStepType.ElectrodesAssigned, Input(assembly: assembly.Id));
        Assert.NotNull((await _db.CatalogItems.FindAsync(assembly.Id))!.FrozenAt);

        // Undoing the assignment doesn't thaw it: the history still points at it.
        await _checklist.UndoAsync("m", _unit.SerialNumber, ProductionStepType.ElectrodesAssigned, null, false);
        Assert.NotNull((await _db.CatalogItems.FindAsync(assembly.Id))!.FrozenAt);

        // And its contents can no longer be edited.
        var part = (await _catalog.CreateAsync("m", CatalogKind.Part, "P9", "Extra", null, null)).Value!;
        Assert.Equal(ChecklistStatus.Conflict, (await _catalog.AddChildAsync("m", assembly.Id, part.Id, 1)).Status);
    }

    [Fact]
    public async Task Assigning_a_superseded_assembly_warns_and_names_the_newest_revision_but_saves()
    {
        var old = await NewAssemblyAsync("A1");
        var newer = await NewAssemblyAsync("A2");
        var newest = await NewAssemblyAsync("A3");
        await _catalog.AddSupersessionAsync("m", newer.Id, old.Id);
        await _catalog.AddSupersessionAsync("m", newest.Id, newer.Id);

        var result = await Mark(ProductionStepType.ElectrodesAssigned, Input(assembly: old.Id));
        Assert.True(result.IsOk);
        var warning = Assert.Single(result.Warnings!);
        Assert.Contains("A3", warning);
        Assert.DoesNotContain("A2", warning);
    }

    [Fact]
    public async Task Changing_the_assembly_of_a_step_records_old_and_new_part_numbers()
    {
        var first = await NewAssemblyAsync("A1");
        var second = await NewAssemblyAsync("A2");
        await Mark(ProductionStepType.MountingAssigned, Input(assembly: first.Id));

        await _checklist.EditAsync("m", _unit.SerialNumber, ProductionStepType.MountingAssigned, Input(assembly: second.Id), false);

        var edited = (await _checklist.HistoryAsync(_unit.Id)).Single(e => e.Type == BciDeviceEventType.StepEdited);
        Assert.Contains("assembly A1 to A2", edited.Note);
        Assert.NotNull((await _db.CatalogItems.FindAsync(second.Id))!.FrozenAt);
    }

    // ---- tags: which assemblies a step accepts ---------------------------------------------------------------------

    [Fact]
    public async Task A_step_only_accepts_assemblies_with_the_tag_it_requires()
    {
        var electrodes = await NewAssemblyAsync("E1", tags: "electrode-set");
        var clip = await NewAssemblyAsync("C1", tags: "mounting-clip");
        var untagged = await NewAssemblyAsync("U1", tags: "");

        Assert.True((await Mark(ProductionStepType.ElectrodesAssigned, Input(assembly: electrodes.Id))).IsOk);
        Assert.True((await Mark(ProductionStepType.MountingAssigned, Input(assembly: clip.Id))).IsOk);

        var other = NewUnit("MSV2G0000037", BciDeviceStatus.Reserved);
        var wrongForElectrodes = await Mark(ProductionStepType.ElectrodesAssigned, Input(assembly: clip.Id), serial: other.SerialNumber);
        Assert.Equal(ChecklistStatus.BadRequest, wrongForElectrodes.Status);
        Assert.Contains("electrode-set", wrongForElectrodes.Message);
        Assert.Equal(ChecklistStatus.BadRequest,
            (await Mark(ProductionStepType.MountingAssigned, Input(assembly: electrodes.Id), serial: other.SerialNumber)).Status);
        Assert.Equal(ChecklistStatus.BadRequest,
            (await Mark(ProductionStepType.ElectrodesAssigned, Input(assembly: untagged.Id), serial: other.SerialNumber)).Status);
    }

    [Fact]
    public async Task The_tag_is_required_when_editing_a_step_to_another_assembly_too()
    {
        var first = await NewAssemblyAsync("E1", tags: "electrode-set");
        var wrong = await NewAssemblyAsync("W1", tags: "something-else");
        await Mark(ProductionStepType.ElectrodesAssigned, Input(assembly: first.Id));

        var edit = await _checklist.EditAsync("m", _unit.SerialNumber, ProductionStepType.ElectrodesAssigned, Input(assembly: wrong.Id), false);
        Assert.Equal(ChecklistStatus.BadRequest, edit.Status);
    }

    [Fact]
    public async Task Assigning_locks_the_required_tag_for_good_but_other_tags_stay_editable()
    {
        var assembly = await NewAssemblyAsync("E1", tags: "electrode-set,left-hand,draft-note");
        await Mark(ProductionStepType.ElectrodesAssigned, Input(assembly: assembly.Id));

        var locked = await _catalog.SetTagsAsync("m", assembly.Id, "left-hand");
        Assert.Equal(ChecklistStatus.Conflict, locked.Status);
        Assert.Contains("electrode-set", locked.Message);

        // Adding, and removing the tags nothing relied on, is fine.
        Assert.True((await _catalog.SetTagsAsync("m", assembly.Id, "electrode-set,right-hand")).IsOk);

        // Undoing the assignment does not thaw it: history still points at the assembly.
        await _checklist.UndoAsync("m", _unit.SerialNumber, ProductionStepType.ElectrodesAssigned, null, false);
        Assert.Equal(ChecklistStatus.Conflict, (await _catalog.SetTagsAsync("m", assembly.Id, "right-hand")).Status);
    }

    [Fact]
    public async Task Only_the_tag_a_step_relied_on_is_locked_not_the_assemblys_other_uses()
    {
        // The same assembly carries both step tags and is assigned at the mounting step only.
        var assembly = await NewAssemblyAsync("B1", tags: "electrode-set,mounting-clip");
        await Mark(ProductionStepType.MountingAssigned, Input(assembly: assembly.Id));

        Assert.True((await _catalog.SetTagsAsync("m", assembly.Id, "mounting-clip")).IsOk); // electrode-set was never relied on
        Assert.Equal(ChecklistStatus.Conflict, (await _catalog.SetTagsAsync("m", assembly.Id, "")).Status);
    }

    [Fact]
    public async Task A_unit_keeps_an_assembly_whose_tag_is_unchanged_and_requirements_can_be_switched_off()
    {
        var assembly = await NewAssemblyAsync("E1", tags: "electrode-set");
        await Mark(ProductionStepType.ElectrodesAssigned, Input(assembly: assembly.Id));
        var edit = await _checklist.EditAsync("m", _unit.SerialNumber, ProductionStepType.ElectrodesAssigned, Input(note: "n", assembly: assembly.Id), false);
        Assert.True(edit.IsOk);

        // With no tag configured for a step, any assembly is accepted there.
        var open = new ProductionChecklist(_db, _clock, _catalog, Microsoft.Extensions.Options.Options.Create(
            new ManufacturingOptions { StepTags = { ["MountingAssigned"] = "" } }));
        Assert.Null(open.RequiredTag(ProductionStepType.MountingAssigned));
        Assert.Equal("electrode-set", open.RequiredTag(ProductionStepType.ElectrodesAssigned));
        var plain = await NewAssemblyAsync("P1", tags: "");
        Assert.True((await open.MarkDoneAsync("m", _unit.SerialNumber, ProductionStepType.MountingAssigned, Input(assembly: plain.Id), false)).IsOk);
    }

    [Fact]
    public async Task Notes_are_length_and_control_character_checked()
    {
        Assert.Equal(ChecklistStatus.BadRequest,
            (await Mark(ProductionStepType.QcPassed, Input(note: new string('x', ProductionChecklist.MaxNoteLength + 1)))).Status);
        Assert.Equal(ChecklistStatus.BadRequest, (await Mark(ProductionStepType.QcPassed, Input(note: "a\0b"))).Status);
        Assert.True((await Mark(ProductionStepType.QcPassed, Input(note: "line one\nline two"))).IsOk);
    }

    // ---- edit and undo ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Editing_changes_the_date_and_note_keeps_the_original_account_and_records_it()
    {
        var first = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        await Mark(ProductionStepType.CasingAssembled, Input(first));

        var later = new DateTimeOffset(2026, 10, 2, 8, 0, 0, TimeSpan.Zero);
        var edit = await _checklist.EditAsync("other", _unit.SerialNumber, ProductionStepType.CasingAssembled, Input(later, "fixed date"), false);

        Assert.True(edit.IsOk);
        Assert.Equal(later, edit.Value!.PerformedAt);
        Assert.Equal("m", edit.Value.PerformedByUserId); // "performed by" stays an honest statement
        var events = await _checklist.HistoryAsync(_unit.Id);
        var changed = events.Single(e => e.Type == BciDeviceEventType.StepEdited);
        Assert.Equal("other", changed.UserId);
        Assert.Contains("2026-10-01 08:00 UTC to 2026-10-02 08:00 UTC", changed.Note);
    }

    [Fact]
    public async Task An_edit_that_changes_nothing_adds_no_event()
    {
        var at = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        await Mark(ProductionStepType.CasingAssembled, Input(at));
        await _checklist.EditAsync("m", _unit.SerialNumber, ProductionStepType.CasingAssembled, Input(at), false);
        Assert.DoesNotContain(await _checklist.HistoryAsync(_unit.Id), e => e.Type == BciDeviceEventType.StepEdited);
    }

    [Fact]
    public async Task Editing_a_step_that_is_not_done_is_refused()
    {
        var edit = await _checklist.EditAsync("m", _unit.SerialNumber, ProductionStepType.QcPassed, Input(), false);
        Assert.Equal(ChecklistStatus.Conflict, edit.Status);
    }

    [Fact]
    public async Task Undo_works_in_any_order_removes_the_row_and_records_the_reason()
    {
        await Mark(ProductionStepType.CasingManufactured);
        await Mark(ProductionStepType.CasingAssembled);

        // Not from the end: the earlier step goes first.
        var undo = await _checklist.UndoAsync("m", _unit.SerialNumber, ProductionStepType.CasingManufactured, "wrong unit", false);
        Assert.True(undo.IsOk);

        var list = await _checklist.GetAsync(_unit.SerialNumber);
        Assert.Equal([ProductionStepType.CasingAssembled], list!.Steps.Select(s => s.Step));
        var e = (await _checklist.HistoryAsync(_unit.Id)).Single(x => x.Type == BciDeviceEventType.StepUndone);
        Assert.Equal("wrong unit", e.Note);

        // And it can be marked again afterwards.
        Assert.True((await Mark(ProductionStepType.CasingManufactured)).IsOk);
    }

    // ---- paired units ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_paired_unit_needs_the_confirmation_for_every_change_and_the_event_says_so()
    {
        var paired = NewUnit("MSV2G0000045", BciDeviceStatus.Manufactured);
        Pair(paired);

        var refused = await Mark(ProductionStepType.QcPassed, serial: paired.SerialNumber);
        Assert.Equal(ChecklistStatus.NeedsPairedConfirmation, refused.Status);
        Assert.Empty((await _checklist.GetAsync(paired.SerialNumber))!.Steps);

        Assert.True((await Mark(ProductionStepType.QcPassed, confirm: true, serial: paired.SerialNumber)).IsOk);
        Assert.True((await _checklist.HistoryAsync(paired.Id)).Single().WasPaired);

        Assert.Equal(ChecklistStatus.NeedsPairedConfirmation,
            (await _checklist.EditAsync("m", paired.SerialNumber, ProductionStepType.QcPassed, Input(note: "x"), false)).Status);
        Assert.Equal(ChecklistStatus.NeedsPairedConfirmation,
            (await _checklist.UndoAsync("m", paired.SerialNumber, ProductionStepType.QcPassed, null, false)).Status);
    }

    [Fact]
    public async Task An_unassigned_pairing_no_longer_needs_the_confirmation()
    {
        var unit = NewUnit("MSV2G0000052", BciDeviceStatus.Manufactured);
        Pair(unit);
        var pairing = _db.AccountDevices.Single(a => a.BciDeviceId == unit.Id);
        pairing.UnassignedAt = _clock.GetUtcNow();
        _db.SaveChanges();

        Assert.True((await Mark(ProductionStepType.QcPassed, serial: unit.SerialNumber)).IsOk);
    }

    // ---- the flash step --------------------------------------------------------------------------------------------

    [Fact]
    public async Task RecordFlash_writes_step_one_once_and_never_moves_it()
    {
        var first = _clock.GetUtcNow();
        ProductionChecklist.RecordFlash(_db, _unit, "m", first, ProductionStepSource.Quarry);
        await _db.SaveChangesAsync();

        // A rekey and re-confirm later records nothing new.
        ProductionChecklist.RecordFlash(_db, _unit, "m", first.AddDays(5), ProductionStepSource.Quarry);
        await _db.SaveChangesAsync();

        var step = Assert.Single(_db.ProductionSteps.Where(s => s.BciDeviceId == _unit.Id));
        Assert.Equal(ProductionStepType.ElectronicsFlashed, step.Step);
        Assert.Equal(first, step.PerformedAt);
        Assert.Equal(ProductionStepSource.Quarry, step.Source);
        Assert.Single(await _checklist.HistoryAsync(_unit.Id));
    }
}
