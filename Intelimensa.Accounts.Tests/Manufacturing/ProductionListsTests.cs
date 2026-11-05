using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Manufacturing;
using Intelimensa.Accounts.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;


namespace Intelimensa.Accounts.Tests.Manufacturing;

public class ProductionListsTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ApplicationDbContext _db;
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
    private readonly ElectrodeSetService _sets;
    private readonly MountingTypeService _mountings;

    public ProductionListsTests()
    {
        _connection.Open();
        _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser { Id = "m", UserName = "m" });
        _db.SaveChanges();
        _sets = new ElectrodeSetService(_db, _clock);
        _mountings = new MountingTypeService(_db, _clock);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<ElectrodeSet> NewSet(string name = "Standard", string version = "A") =>
        (await _sets.CreateAsync("m", name, version, "desc")).Value!;

    private async Task<ElectrodeAssembly> NewAssembly(ElectrodeSet set, string name = "Recording", int count = 2) =>
        (await _sets.AddAssemblyAsync("m", set.Id, name, "1.0", "PN-1", null, count)).Value!;

    private void Freeze(ElectrodeSet set)
    {
        set.FrozenAt = _clock.GetUtcNow();
        _db.SaveChanges();
    }

    // ---- mounting types --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Mounting_types_get_a_unique_slug_and_can_be_renamed_but_keep_their_key()
    {
        var a = (await _mountings.AddAsync("Padded clips")).Value!;
        Assert.Equal("padded-clips", a.Key);
        Assert.Equal(ChecklistStatus.Conflict, (await _mountings.AddAsync("padded CLIPS")).Status);

        await _mountings.RenameAsync(a.Id, "Padded clips (new)");
        var renamed = await _db.MountingTypes.FindAsync(a.Id);
        Assert.Equal("Padded clips (new)", renamed!.DisplayName);
        Assert.Equal("padded-clips", renamed.Key);

        var b = (await _mountings.AddAsync("Padded-clips")).Value!;
        Assert.Equal("padded-clips-2", b.Key);
    }

    [Fact]
    public async Task Retiring_a_mounting_type_is_the_only_way_out_and_is_idempotent()
    {
        var a = (await _mountings.AddAsync("Bose tight clips")).Value!;
        var first = await _mountings.RetireAsync("m", a.Id);
        Assert.NotNull(first.Value!.RetiredAt);
        var at = first.Value.RetiredAt;

        _clock.Advance(TimeSpan.FromDays(1));
        await _mountings.RetireAsync("m", a.Id);
        Assert.Equal(at, (await _db.MountingTypes.FindAsync(a.Id))!.RetiredAt);
    }

    // ---- sets ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_set_needs_a_name_and_version_and_the_pair_is_unique_case_insensitively()
    {
        Assert.Equal(ChecklistStatus.BadRequest, (await _sets.CreateAsync("m", " ", "A", null)).Status);
        Assert.Equal(ChecklistStatus.BadRequest, (await _sets.CreateAsync("m", "Standard", "", null)).Status);

        await NewSet("Standard", "A");
        Assert.Equal(ChecklistStatus.Conflict, (await _sets.CreateAsync("m", "standard", "a", null)).Status);
        Assert.True((await _sets.CreateAsync("m", "Standard", "B", null)).IsOk);
    }

    [Fact]
    public async Task Version_is_free_text_with_no_format_rule()
    {
        foreach (var v in new[] { "A", "1.0", "rev c", "2026-11", "v1 (draft)" })
            Assert.True((await _sets.CreateAsync("m", "Set " + v, v, null)).IsOk, v);
    }

    [Fact]
    public async Task Edits_stamp_the_account_and_time_that_last_modified_the_set()
    {
        var set = await NewSet();
        _db.Users.Add(new ApplicationUser { Id = "m2", UserName = "m2" });
        _db.SaveChanges();

        _clock.Advance(TimeSpan.FromHours(3));
        await _sets.AddAssemblyAsync("m2", set.Id, "Reference", "1", null, null, 1);

        var fresh = await _sets.GetAsync(set.Id);
        Assert.Equal("m2", fresh!.LastModifiedByUserId);
        Assert.Equal(_clock.GetUtcNow(), fresh.LastModifiedAt);
        Assert.Equal("m", fresh.CreatedByUserId);
    }

    [Fact]
    public async Task Assemblies_and_parts_validate_counts_and_uniqueness()
    {
        var set = await NewSet();
        var asm = await NewAssembly(set);

        Assert.Equal(ChecklistStatus.BadRequest, (await _sets.AddAssemblyAsync("m", set.Id, "X", "1", null, null, 0)).Status);
        Assert.Equal(ChecklistStatus.BadRequest,
            (await _sets.AddAssemblyAsync("m", set.Id, "X", "1", null, null, ElectrodeSetService.MaxAssemblyCount + 1)).Status);
        Assert.Equal(ChecklistStatus.Conflict, (await _sets.AddAssemblyAsync("m", set.Id, "recording", "1.0", null, null, 1)).Status);
        // Same name, another version: fine.
        Assert.True((await _sets.AddAssemblyAsync("m", set.Id, "Recording", "2.0", null, null, 1)).IsOk);

        Assert.True((await _sets.AddPartAsync("m", asm.Id, "Hub", "1", "H-1", null, 1)).IsOk);
        Assert.Equal(ChecklistStatus.Conflict, (await _sets.AddPartAsync("m", asm.Id, "hub", "1", null, null, 1)).Status);
        Assert.Equal(ChecklistStatus.BadRequest, (await _sets.AddPartAsync("m", asm.Id, "Cup", "1", null, null, 1000)).Status);
        Assert.Equal(ChecklistStatus.BadRequest, (await _sets.AddPartAsync("m", asm.Id, "Cup\u0007", "1", null, null, 1)).Status);
    }

    [Fact]
    public async Task Assemblies_and_parts_keep_the_order_they_were_added_in()
    {
        var set = await NewSet();
        var asm = await NewAssembly(set);
        foreach (var n in new[] { "Plug", "Wires", "Hub" })
            await _sets.AddPartAsync("m", asm.Id, n, "1", null, null, 1);
        await _sets.AddAssemblyAsync("m", set.Id, "Reference", "1", null, null, 1);

        var fresh = await _sets.GetAsync(set.Id);
        Assert.Equal(["Recording", "Reference"], fresh!.Assemblies.Select(a => a.Name));
        Assert.Equal(["Plug", "Wires", "Hub"], fresh.Assemblies[0].Parts.Select(p => p.Name));
    }

    [Fact]
    public async Task The_part_number_is_optional_and_not_unique()
    {
        var set = await NewSet();
        var asm = await NewAssembly(set);
        Assert.True((await _sets.AddPartAsync("m", asm.Id, "Arm", "1", "SAME", null, 6)).IsOk);
        Assert.True((await _sets.AddPartAsync("m", asm.Id, "Cup", "1", "SAME", null, 7)).IsOk);
        Assert.True((await _sets.AddPartAsync("m", asm.Id, "Wires", "1", null, null, 1)).IsOk);
    }

    [Fact]
    public async Task Limits_on_assemblies_per_set_and_parts_per_assembly_apply()
    {
        var set = await NewSet();
        for (var i = 0; i < ElectrodeSetService.MaxAssemblies; i++)
            Assert.True((await _sets.AddAssemblyAsync("m", set.Id, $"A{i}", "1", null, null, 1)).IsOk);
        Assert.Equal(ChecklistStatus.BadRequest, (await _sets.AddAssemblyAsync("m", set.Id, "one too many", "1", null, null, 1)).Status);

        var asm = (await _sets.GetAsync(set.Id))!.Assemblies[0];
        for (var i = 0; i < ElectrodeSetService.MaxParts; i++)
            Assert.True((await _sets.AddPartAsync("m", asm.Id, $"P{i}", "1", null, null, 1)).IsOk);
        Assert.Equal(ChecklistStatus.BadRequest, (await _sets.AddPartAsync("m", asm.Id, "one too many", "1", null, null, 1)).Status);
    }

    [Fact]
    public async Task Removing_an_assembly_removes_its_parts_in_an_editable_set()
    {
        var set = await NewSet();
        var asm = await NewAssembly(set);
        await _sets.AddPartAsync("m", asm.Id, "Hub", "1", null, null, 1);

        Assert.True((await _sets.RemoveAssemblyAsync("m", asm.Id)).IsOk);
        Assert.Empty(_db.ElectrodeAssemblies);
        Assert.Empty(_db.ElectrodeParts);
    }

    // ---- freezing, revisions, retiring -----------------------------------------------------------------------------

    [Fact]
    public async Task A_frozen_set_refuses_every_edit()
    {
        var set = await NewSet();
        var asm = await NewAssembly(set);
        var part = (await _sets.AddPartAsync("m", asm.Id, "Hub", "1", null, null, 1)).Value!;
        Freeze(set);

        Assert.Equal(ChecklistStatus.Conflict, (await _sets.UpdateAsync("m", set.Id, "New", "A", null)).Status);
        Assert.Equal(ChecklistStatus.Conflict, (await _sets.AddAssemblyAsync("m", set.Id, "More", "1", null, null, 1)).Status);
        Assert.Equal(ChecklistStatus.Conflict, (await _sets.UpdateAssemblyAsync("m", asm.Id, "Recording", "1.0", null, null, 5)).Status);
        Assert.Equal(ChecklistStatus.Conflict, (await _sets.RemoveAssemblyAsync("m", asm.Id)).Status);
        Assert.Equal(ChecklistStatus.Conflict, (await _sets.AddPartAsync("m", asm.Id, "Cup", "1", null, null, 1)).Status);
        Assert.Equal(ChecklistStatus.Conflict, (await _sets.UpdatePartAsync("m", part.Id, "Hub", "1", null, null, 9)).Status);
        Assert.Equal(ChecklistStatus.Conflict, (await _sets.RemovePartAsync("m", part.Id)).Status);
        Assert.Equal(ChecklistStatus.Conflict, (await _sets.CopyAssemblyAsync("m", set.Id, asm.Id)).Status);

        var fresh = await _sets.GetAsync(set.Id);
        Assert.Equal(2, fresh!.Assemblies[0].Count);
        Assert.Single(fresh.Assemblies[0].Parts);
    }

    [Fact]
    public async Task A_new_revision_copies_a_frozen_set_with_everything_and_is_editable()
    {
        var set = await NewSet();
        var asm = await NewAssembly(set);
        await _sets.AddPartAsync("m", asm.Id, "Hub", "1", "H-1", "central", 1);
        await _sets.AddPartAsync("m", asm.Id, "Cup", "2", null, null, 7);
        Freeze(set);

        var rev = (await _sets.NewRevisionAsync("m", set.Id, "B")).Value!;
        Assert.Equal(set.Id, rev.PreviousId);
        Assert.Equal("Standard", rev.Name);
        Assert.Equal("B", rev.Version);
        Assert.Null(rev.FrozenAt);

        var copy = (await _sets.GetAsync(rev.Id))!;
        Assert.Equal(set.Description, copy.Description);
        var a = Assert.Single(copy.Assemblies);
        Assert.NotEqual(asm.Id, a.Id);
        Assert.Equal(2, a.Count);
        Assert.Equal(["Hub", "Cup"], a.Parts.Select(p => p.Name));
        Assert.Equal(["H-1", null], a.Parts.Select(p => p.PartNumber));
        Assert.Equal([1, 7], a.Parts.Select(p => p.Count));

        // The revision is independent: editing it leaves the original alone.
        await _sets.UpdateAssemblyAsync("m", a.Id, "Recording", "1.0", null, null, 9);
        Assert.Equal(2, (await _sets.GetAsync(set.Id))!.Assemblies[0].Count);
    }

    [Fact]
    public async Task A_revision_needs_a_version_that_makes_the_pair_new()
    {
        var set = await NewSet("Standard", "A");
        Assert.Equal(ChecklistStatus.Conflict, (await _sets.NewRevisionAsync("m", set.Id, "a")).Status);
        Assert.Equal(ChecklistStatus.BadRequest, (await _sets.NewRevisionAsync("m", set.Id, " ")).Status);
        Assert.Equal(ChecklistStatus.NotFound, (await _sets.NewRevisionAsync("m", Guid.NewGuid(), "B")).Status);
    }

    [Fact]
    public async Task A_retired_set_cannot_be_edited_and_retiring_is_idempotent()
    {
        var set = await NewSet();
        var first = (await _sets.RetireAsync("m", set.Id)).Value!.RetiredAt;
        Assert.NotNull(first);
        _clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(first, (await _sets.RetireAsync("m", set.Id)).Value!.RetiredAt);

        Assert.Equal(ChecklistStatus.Conflict, (await _sets.AddAssemblyAsync("m", set.Id, "X", "1", null, null, 1)).Status);
        // A retired set can still be copied into a revision.
        Assert.True((await _sets.NewRevisionAsync("m", set.Id, "B")).IsOk);
    }

    [Fact]
    public async Task Copying_an_assembly_between_sets_makes_a_copy_not_a_link()
    {
        var source = await NewSet("Standard", "A");
        var asm = await NewAssembly(source, "Reference", 1);
        await _sets.AddPartAsync("m", asm.Id, "Swingarm", "1", null, null, 1);
        Freeze(source); // a frozen set is a fine source

        var target = await NewSet("Other", "A");
        var copied = (await _sets.CopyAssemblyAsync("m", target.Id, asm.Id)).Value!;
        Assert.NotEqual(asm.Id, copied.Id);
        Assert.Equal(target.Id, copied.ElectrodeSetId);

        await _sets.AddPartAsync("m", copied.Id, "Extra", "1", null, null, 1);
        Assert.Single((await _sets.GetAsync(source.Id))!.Assemblies[0].Parts);
        Assert.Equal(2, (await _sets.GetAsync(target.Id))!.Assemblies[0].Parts.Count);

        // Copying the same assembly again collides on name + version.
        Assert.Equal(ChecklistStatus.Conflict, (await _sets.CopyAssemblyAsync("m", target.Id, asm.Id)).Status);
    }

    [Fact]
    public async Task Duplicating_an_assembly_makes_an_independent_copy_in_the_same_set_under_a_new_name()
    {
        var set = await NewSet();
        var left = await NewAssembly(set, "Left recording", 1);
        await _sets.AddPartAsync("m", left.Id, "Hub", "1", "H-1", "central", 1);
        await _sets.AddPartAsync("m", left.Id, "Cup", "1", null, null, 7);

        var right = (await _sets.DuplicateAssemblyAsync("m", left.Id, "Right recording")).Value!;
        Assert.Equal(set.Id, right.ElectrodeSetId);
        Assert.Equal("1.0", right.Version);
        Assert.Equal(left.PartNumber, right.PartNumber);
        Assert.Equal(1, right.Count);

        var fresh = await _sets.GetAsync(set.Id);
        Assert.Equal(["Left recording", "Right recording"], fresh!.Assemblies.Select(a => a.Name));
        var copy = fresh.Assemblies[1];
        Assert.Equal(["Hub", "Cup"], copy.Parts.Select(p => p.Name));
        Assert.Equal([1, 7], copy.Parts.Select(p => p.Count));

        // Change a part on the right one only.
        var cup = copy.Parts.Single(p => p.Name == "Cup");
        await _sets.UpdatePartAsync("m", cup.Id, "Cup", "2", null, null, 6);
        fresh = await _sets.GetAsync(set.Id);
        Assert.Equal([7, 6], fresh!.Assemblies.Select(a => a.Parts.Single(p => p.Name == "Cup").Count));
    }

    [Fact]
    public async Task Duplicating_needs_a_new_name_and_version_pair_and_an_editable_set()
    {
        var set = await NewSet();
        var left = await NewAssembly(set, "Left recording", 1);

        Assert.Equal(ChecklistStatus.Conflict, (await _sets.DuplicateAssemblyAsync("m", left.Id, "left RECORDING")).Status);
        Assert.Equal(ChecklistStatus.BadRequest, (await _sets.DuplicateAssemblyAsync("m", left.Id, " ")).Status);
        // The same name with another version is a different assembly.
        Assert.True((await _sets.DuplicateAssemblyAsync("m", left.Id, "Left recording", "2.0")).IsOk);

        Freeze(set);
        Assert.Equal(ChecklistStatus.Conflict, (await _sets.DuplicateAssemblyAsync("m", left.Id, "Right recording")).Status);
        Assert.Equal(ChecklistStatus.NotFound, (await _sets.DuplicateAssemblyAsync("m", Guid.NewGuid(), "X")).Status);
    }

    [Fact]
    public async Task The_list_counts_units_per_set()
    {
        var set = await NewSet();
        var unit = new BciDevice { SerialNumber = "MSV2G0000011", DeviceType = "msv2" };
        _db.BciDevices.Add(unit);
        _db.SaveChanges();
        _db.ProductionSteps.Add(new ProductionStep
        {
            BciDeviceId = unit.Id, Step = ProductionStepType.ElectrodesAssigned, PerformedAt = _clock.GetUtcNow(), ElectrodeSetId = set.Id,
        });
        _db.SaveChanges();

        var row = Assert.Single(await _sets.ListAsync());
        Assert.Equal(1, row.Units);
    }
}
