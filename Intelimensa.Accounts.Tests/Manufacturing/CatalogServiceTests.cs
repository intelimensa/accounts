using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Manufacturing;
using Intelimensa.Accounts.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Tests.Manufacturing;

public class CatalogServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ApplicationDbContext _db;
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
    private readonly CatalogService _catalog;

    public CatalogServiceTests()
    {
        _connection.Open();
        _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser { Id = "m", UserName = "m" });
        _db.SaveChanges();
        _catalog = new CatalogService(_db, _clock);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<CatalogItem> Part(string no, string name = "Part") =>
        (await _catalog.CreateAsync("m", CatalogKind.Part, no, name, null, null)).Value!;

    private async Task<CatalogItem> Assembly(string no, string name = "Assembly") =>
        (await _catalog.CreateAsync("m", CatalogKind.Assembly, no, name, null, null)).Value!;

    private Task<ChecklistResult<CatalogChild>> Add(CatalogItem assembly, CatalogItem child, int count = 1) =>
        _catalog.AddChildAsync("m", assembly.Id, child.Id, count);

    // ---- part numbers ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("50002", true)]
    [InlineData("00042", true)]
    [InlineData("AB-12_x", true)]
    [InlineData("  50002  ", true)]
    [InlineData("", false)]
    [InlineData("a b", false)]
    [InlineData("a/b", false)]
    [InlineData("é1", false)]
    public void Part_number_characters(string input, bool valid) => Assert.Equal(valid, PartNumber.TryNormalize(input, out _));

    [Fact]
    public void Part_numbers_are_at_most_64_characters_and_keep_their_leading_zeros()
    {
        Assert.True(PartNumber.TryNormalize(new string('9', 64), out _));
        Assert.False(PartNumber.TryNormalize(new string('9', 65), out _));
        PartNumber.TryNormalize(" 00042 ", out var value);
        Assert.Equal("00042", value); // a string, never a number
    }

    [Fact]
    public async Task A_part_number_is_unique_across_parts_and_assemblies_ignoring_case()
    {
        await Part("X-1");
        Assert.Equal(ChecklistStatus.Conflict, (await _catalog.CreateAsync("m", CatalogKind.Part, "x-1", "Again", null, null)).Status);
        Assert.Equal(ChecklistStatus.Conflict, (await _catalog.CreateAsync("m", CatalogKind.Assembly, "X-1", "Same number, other kind", null, null)).Status);
        Assert.Equal(ChecklistStatus.BadRequest, (await _catalog.CreateAsync("m", CatalogKind.Part, "bad number", "N", null, null)).Status);
        Assert.Equal(ChecklistStatus.BadRequest, (await _catalog.CreateAsync("m", CatalogKind.Part, "Y-1", " ", null, null)).Status);
    }

    [Fact]
    public async Task Fields_are_length_and_control_character_checked()
    {
        Assert.Equal(ChecklistStatus.BadRequest,
            (await _catalog.CreateAsync("m", CatalogKind.Part, "L1", new string('n', 101), null, null)).Status);
        Assert.Equal(ChecklistStatus.BadRequest,
            (await _catalog.CreateAsync("m", CatalogKind.Part, "L2", "n", new string('d', 1001), null)).Status);
        Assert.Equal(ChecklistStatus.BadRequest,
            (await _catalog.CreateAsync("m", CatalogKind.Part, "L3", "n", null, new string('r', 201))).Status);
        Assert.Equal(ChecklistStatus.BadRequest,
            (await _catalog.CreateAsync("m", CatalogKind.Part, "L4", "n\u0007", null, null)).Status);
        Assert.True((await _catalog.CreateAsync("m", CatalogKind.Part, "L5", "front plate with \"L\" marking", null, "cad/10002.step rev C")).IsOk);
    }

    [Fact]
    public async Task Part_number_lookup_ignores_case_and_surrounding_space()
    {
        var part = await Part("AbC-1");
        Assert.Equal(part.Id, (await _catalog.FindAsync(" abc-1 "))!.Id);
        Assert.Null(await _catalog.FindAsync("nope"));
        Assert.Null(await _catalog.FindAsync("bad number"));
    }

    // ---- editing the fields, with a history ------------------------------------------------------------------------

    [Fact]
    public async Task Name_description_and_reference_can_change_and_each_change_is_kept()
    {
        var part = await Part("P1", "Old name");
        _clock.Advance(TimeSpan.FromHours(1));
        await _catalog.UpdateAsync("m", part.Id, "New name", "now with a description", "cad/p1.step");
        await _catalog.UpdateAsync("m", part.Id, "New name", "now with a description", "cad/p1.step"); // nothing changed

        var fresh = (await _catalog.GetAsync(part.Id))!;
        Assert.Equal("New name", fresh.Name);
        Assert.Equal("P1", fresh.PartNo); // the number never changes
        var history = await _catalog.HistoryAsync(part.Id);
        Assert.Equal(3, history.Count);
        Assert.Equal(["Description", "Name", "Reference"], history.Select(h => h.Field).Order());
        var name = history.Single(h => h.Field == "Name");
        Assert.Equal(("Old name", "New name"), (name.OldValue, name.NewValue));
        Assert.Equal(_clock.GetUtcNow(), fresh.LastModifiedAt);
    }

    [Fact]
    public async Task Retiring_is_the_only_way_out_and_is_idempotent_and_blocks_new_references()
    {
        var part = await Part("P1");
        var assembly = await Assembly("A1");
        var first = (await _catalog.RetireAsync("m", part.Id)).Value!.RetiredAt;
        _clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(first, (await _catalog.RetireAsync("m", part.Id)).Value!.RetiredAt);

        Assert.Equal(ChecklistStatus.Conflict, (await Add(assembly, part)).Status);
    }

    // ---- an assembly's contents ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_part_has_no_contents_and_a_draft_assembly_can_be_edited_freely()
    {
        var part = await Part("P1");
        var other = await Part("P2");
        var assembly = await Assembly("A1");

        Assert.Equal(ChecklistStatus.BadRequest, (await Add(part, other)).Status);

        Assert.True((await Add(assembly, part, 4)).IsOk);
        Assert.True((await Add(assembly, other)).IsOk);
        Assert.True((await _catalog.SetChildCountAsync("m", assembly.Id, part.Id, 6)).IsOk);
        Assert.True((await _catalog.RemoveChildAsync("m", assembly.Id, other.Id)).IsOk);

        var graph = await _catalog.LoadGraphAsync();
        var line = Assert.Single(graph.ChildrenOf(assembly.Id));
        Assert.Equal((part.Id, 6), (line.ChildId, line.Count));
    }

    [Fact]
    public async Task Counts_are_from_one_to_9999_and_a_child_is_listed_once()
    {
        var part = await Part("P1");
        var assembly = await Assembly("A1");
        Assert.Equal(ChecklistStatus.BadRequest, (await Add(assembly, part, 0)).Status);
        Assert.Equal(ChecklistStatus.BadRequest, (await Add(assembly, part, 10_000)).Status);
        Assert.True((await Add(assembly, part, 9999)).IsOk);
        Assert.Equal(ChecklistStatus.Conflict, (await Add(assembly, part)).Status);
        Assert.Equal(ChecklistStatus.BadRequest, (await _catalog.SetChildCountAsync("m", assembly.Id, part.Id, 0)).Status);
    }

    [Fact]
    public async Task Children_keep_the_order_they_were_added_in()
    {
        var assembly = await Assembly("A1");
        foreach (var no in new[] { "P3", "P1", "P2" })
            await Add(assembly, await Part(no));

        var graph = await _catalog.LoadGraphAsync();
        Assert.Equal(["P3", "P1", "P2"], graph.ChildrenOf(assembly.Id).Select(c => graph.Items[c.ChildId].PartNo));
    }

    [Fact]
    public async Task Listing_an_assembly_freezes_it_and_a_frozen_assembly_refuses_every_content_change()
    {
        var part = await Part("P1");
        var extra = await Part("P2");
        var child = await Assembly("C1");
        await Add(child, part);
        var parent = await Assembly("A1");
        Assert.Null((await _catalog.GetAsync(child.Id))!.FrozenAt);

        await Add(parent, child);
        Assert.NotNull((await _catalog.GetAsync(child.Id))!.FrozenAt);
        Assert.Null((await _catalog.GetAsync(parent.Id))!.FrozenAt); // the parent is still a draft

        Assert.Equal(ChecklistStatus.Conflict, (await Add(child, extra)).Status);
        Assert.Equal(ChecklistStatus.Conflict, (await _catalog.SetChildCountAsync("m", child.Id, part.Id, 3)).Status);
        Assert.Equal(ChecklistStatus.Conflict, (await _catalog.RemoveChildAsync("m", child.Id, part.Id)).Status);
        Assert.True((await Add(parent, extra)).IsOk);
    }

    [Fact]
    public async Task A_frozen_assembly_stays_frozen_when_the_listing_is_removed()
    {
        var part = await Part("P1");
        var child = await Assembly("C1");
        await Add(child, part);
        var parent = await Assembly("A1");
        await Add(parent, child);
        await _catalog.RemoveChildAsync("m", parent.Id, child.Id);
        Assert.NotNull((await _catalog.GetAsync(child.Id))!.FrozenAt);
    }

    [Fact]
    public async Task Name_and_description_of_a_frozen_assembly_can_still_be_edited()
    {
        var part = await Part("P1");
        var child = await Assembly("C1");
        await Add(child, part);
        await Add(await Assembly("A1"), child);

        Assert.True((await _catalog.UpdateAsync("m", child.Id, "Better name", "d", null)).IsOk);
        Assert.Equal("Better name", (await _catalog.GetAsync(child.Id))!.Name);
    }

    [Fact]
    public async Task A_retired_assembly_cannot_be_edited_either()
    {
        var part = await Part("P1");
        var assembly = await Assembly("A1");
        await _catalog.RetireAsync("m", assembly.Id);
        Assert.Equal(ChecklistStatus.Conflict, (await Add(assembly, part)).Status);
    }

    // ---- cycles, depth, limits -------------------------------------------------------------------------------------

    [Fact]
    public async Task An_assembly_cannot_contain_itself()
    {
        var a = await Assembly("A");
        Assert.Equal(ChecklistStatus.Conflict, (await Add(a, a)).Status);
    }

    [Fact]
    public async Task A_cycle_cannot_be_built_because_listing_freezes_the_child()
    {
        // a > b > c. Closing the loop would mean adding a under c, but c was frozen the moment b listed it,
        // so the edit is refused as frozen before any cycle check is needed. (The import checks cycles
        // separately, on files: see CatalogXmlTests.)
        var part = await Part("P1");
        var a = await Assembly("A");
        var b = await Assembly("B");
        var c = await Assembly("C");
        await Add(c, part);
        await Add(b, c);
        await Add(a, b);

        Assert.Equal(ChecklistStatus.Conflict, (await Add(c, a)).Status);
        Assert.Equal(ChecklistStatus.Conflict, (await Add(b, a)).Status);
    }

    [Fact]
    public async Task Nesting_is_limited_to_twenty_levels()
    {
        var part = await Part("P0");
        var below = await Assembly("L1");
        await Add(below, part); // height 2
        for (var level = 2; level <= CatalogService.MaxDepth - 1; level++)
        {
            var next = await Assembly($"L{level}");
            Assert.True((await Add(next, below)).IsOk, $"level {level}");
            below = next;
        }
        // below now has height 20 (19 assemblies and the part).
        var top = await Assembly("TOP");
        Assert.Equal(ChecklistStatus.BadRequest, (await Add(top, below)).Status);
    }

    [Fact]
    public async Task An_assembly_has_at_most_500_lines()
    {
        var assembly = await Assembly("A1");
        for (var i = 0; i < CatalogService.MaxChildren; i++)
            Assert.True((await Add(assembly, await Part($"P{i}"))).IsOk);
        Assert.Equal(ChecklistStatus.BadRequest, (await Add(assembly, await Part("ONE-TOO-MANY"))).Status);
    }

    // ---- trees and quantities --------------------------------------------------------------------------------------

    [Fact]
    public async Task Quantities_multiply_down_the_tree_and_add_across_branches()
    {
        var screw = await Part("SCREW");
        var cup = await Part("CUP");
        var terminal = await Assembly("TERMINAL");
        await Add(terminal, screw, 2);
        await Add(terminal, cup);
        var arm = await Assembly("ARM");
        await Add(arm, screw, 3);
        var bundle = await Assembly("BUNDLE");
        await Add(bundle, terminal, 4);
        await Add(bundle, arm, 2);
        await Add(bundle, screw, 1);

        var graph = await _catalog.LoadGraphAsync();
        var tree = graph.Tree(bundle.Id);
        Assert.Equal(1, tree.Total);
        Assert.Equal(4, tree.Children.Single(c => c.Item.PartNo == "TERMINAL").Total);

        var flat = graph.Flatten(bundle.Id).ToDictionary(l => l.Part.PartNo, l => l.Total);
        Assert.Equal(4 * 2 + 2 * 3 + 1, flat["SCREW"]); // 15
        Assert.Equal(4, flat["CUP"]);
        Assert.Equal(2, flat.Count);
    }

    [Fact]
    public async Task One_sub_assembly_can_be_reused_in_several_places()
    {
        var part = await Part("P1");
        var shared = await Assembly("SHARED");
        await Add(shared, part);
        var left = await Assembly("LEFT");
        var right = await Assembly("RIGHT");
        await Add(left, shared);
        await Add(right, shared, 2);

        var graph = await _catalog.LoadGraphAsync();
        Assert.Equal(["LEFT", "RIGHT"], graph.ParentsOf(shared.Id).Select(p => p.PartNo).Order());
        Assert.Equal(2, graph.Flatten(right.Id).Single().Total);
    }

    // ---- supersedes ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Supersedes_links_same_kind_only_and_not_to_itself()
    {
        var oldPart = await Part("P1");
        var newPart = await Part("P2");
        var assembly = await Assembly("A1");

        Assert.True((await _catalog.AddSupersessionAsync("m", newPart.Id, oldPart.Id)).IsOk);
        Assert.True((await _catalog.AddSupersessionAsync("m", newPart.Id, oldPart.Id)).IsOk); // idempotent
        Assert.Equal(ChecklistStatus.BadRequest, (await _catalog.AddSupersessionAsync("m", newPart.Id, newPart.Id)).Status);
        Assert.Equal(ChecklistStatus.BadRequest, (await _catalog.AddSupersessionAsync("m", assembly.Id, oldPart.Id)).Status);
        Assert.Single(_db.CatalogSupersessions);
    }

    [Fact]
    public async Task Supersedes_chains_resolve_to_the_newest_and_cycles_are_refused()
    {
        var a = await Part("P1");
        var b = await Part("P2");
        var c = await Part("P3");
        await _catalog.AddSupersessionAsync("m", b.Id, a.Id);
        await _catalog.AddSupersessionAsync("m", c.Id, b.Id);

        var graph = await _catalog.LoadGraphAsync();
        Assert.Equal(["P3"], graph.Newest(a.Id).Select(i => i.PartNo));
        Assert.Equal(["P3"], graph.Newest(b.Id).Select(i => i.PartNo));
        Assert.Empty(graph.Newest(c.Id));
        Assert.True(graph.IsSuperseded(a.Id));
        Assert.False(graph.IsSuperseded(c.Id));

        Assert.Equal(ChecklistStatus.Conflict, (await _catalog.AddSupersessionAsync("m", a.Id, c.Id)).Status);
    }

    [Fact]
    public async Task One_item_may_supersede_several_and_be_superseded_by_several()
    {
        var a = await Part("P1");
        var b = await Part("P2");
        var merged = await Part("P3");
        var alt = await Part("P4");
        await _catalog.AddSupersessionAsync("m", merged.Id, a.Id);
        await _catalog.AddSupersessionAsync("m", merged.Id, b.Id);
        await _catalog.AddSupersessionAsync("m", alt.Id, a.Id);

        var graph = await _catalog.LoadGraphAsync();
        Assert.Equal(["P3", "P4"], graph.Newest(a.Id).Select(i => i.PartNo));
        Assert.Equal(["P1", "P2"], graph.Supersedes(merged.Id).Select(i => i.PartNo).Order());
    }

    [Fact]
    public async Task Supersedes_can_be_given_when_creating_and_must_exist_with_the_same_kind()
    {
        var old = await Part("P1");
        var ok = await _catalog.CreateAsync("m", CatalogKind.Part, "P2", "New", null, null, ["p1"]);
        Assert.True(ok.IsOk);
        Assert.Equal(["P1"], (await _catalog.LoadGraphAsync()).Supersedes(ok.Value!.Id).Select(i => i.PartNo));

        Assert.Equal(ChecklistStatus.BadRequest, (await _catalog.CreateAsync("m", CatalogKind.Part, "P3", "New", null, null, ["NOPE"])).Status);
        Assert.Equal(ChecklistStatus.BadRequest, (await _catalog.CreateAsync("m", CatalogKind.Assembly, "A9", "New", null, null, [old.PartNo])).Status);
        Assert.Null(await _catalog.FindAsync("P3")); // a refused create leaves nothing behind
    }

    [Fact]
    public async Task A_link_can_be_removed()
    {
        var a = await Part("P1");
        var b = await Part("P2");
        await _catalog.AddSupersessionAsync("m", b.Id, a.Id);
        Assert.True((await _catalog.RemoveSupersessionAsync(b.Id, a.Id)).IsOk);
        Assert.Equal(ChecklistStatus.NotFound, (await _catalog.RemoveSupersessionAsync(b.Id, a.Id)).Status);
    }

    // ---- tags ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("electrode-set", "electrode-set")]
    [InlineData("  Electrode-Set  ", "electrode-set")]
    [InlineData("a_b-9", "a_b-9")]
    public void A_tag_is_trimmed_and_lowercased(string input, string expected)
    {
        Assert.True(CatalogTags.TryNormalize(input, out var tag));
        Assert.Equal(expected, tag);
    }

    [Theory]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData("a/b")]
    [InlineData("é")]
    [InlineData("a,b")]
    public void Bad_tags_are_refused(string input) => Assert.False(CatalogTags.TryNormalize(input, out _));

    [Fact]
    public void A_tag_list_drops_blanks_and_repeats_ignores_case_and_comes_back_sorted()
    {
        var (tags, error) = CatalogTags.ParseList(" Screw, 3d-printed ,, screw ,ABS ");
        Assert.Null(error);
        Assert.Equal(["3d-printed", "abs", "screw"], tags);
        Assert.Equal("3d-printed,abs,screw", CatalogTags.Format(tags));
        Assert.Empty(CatalogTags.ParseList(null).Tags);
        Assert.Empty(CatalogTags.ParseList(" , ,").Tags);
    }

    [Fact]
    public void A_tag_list_refuses_a_bad_tag_a_long_tag_and_too_many_tags()
    {
        Assert.NotNull(CatalogTags.ParseList("good, not good").Error);
        Assert.NotNull(CatalogTags.ParseList(new string('x', 41)).Error);
        Assert.Null(CatalogTags.ParseList(new string('x', 40)).Error);
        var many = string.Join(",", Enumerable.Range(0, CatalogTags.MaxPerItem + 1).Select(i => "t" + i));
        Assert.NotNull(CatalogTags.ParseList(many).Error);
        Assert.Null(CatalogTags.ParseList(string.Join(",", Enumerable.Range(0, CatalogTags.MaxPerItem).Select(i => "t" + i))).Error);
    }

    [Fact]
    public async Task Tags_can_be_given_when_creating_set_edited_and_cleared_with_each_change_in_the_history()
    {
        var created = (await _catalog.CreateAsync("m", CatalogKind.Part, "P1", "Screw", null, null, tags: "Metal, screw")).Value!;
        Assert.Equal(["metal", "screw"], (await _catalog.LoadGraphAsync()).TagsOf(created.Id).Select(t => t.Tag));

        _clock.Advance(TimeSpan.FromHours(1));
        Assert.True((await _catalog.SetTagsAsync("m", created.Id, "screw, m3")).IsOk);
        Assert.Equal(["m3", "screw"], (await _catalog.LoadGraphAsync()).TagsOf(created.Id).Select(t => t.Tag));
        Assert.True((await _catalog.SetTagsAsync("m", created.Id, "screw, m3")).IsOk); // no change, no history
        Assert.True((await _catalog.SetTagsAsync("m", created.Id, "")).IsOk);
        Assert.Empty((await _catalog.LoadGraphAsync()).TagsOf(created.Id));

        var changes = (await _catalog.HistoryAsync(created.Id)).Where(c => c.Field == "Tags").OrderBy(c => c.NewValue ?? "").ToList();
        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, c => c.OldValue == "m3,screw" && c.NewValue is null);
        Assert.Contains(changes, c => c.OldValue == "metal,screw" && c.NewValue == "m3,screw");
        Assert.Equal(_clock.GetUtcNow(), (await _catalog.GetAsync(created.Id))!.LastModifiedAt);
    }

    [Fact]
    public async Task Bad_tags_on_create_or_set_change_nothing()
    {
        Assert.Equal(ChecklistStatus.BadRequest, (await _catalog.CreateAsync("m", CatalogKind.Part, "P1", "x", null, null, tags: "bad tag")).Status);
        Assert.Null(await _catalog.FindAsync("P1")); // a refused create leaves nothing behind

        var part = await Part("P2");
        await _catalog.SetTagsAsync("m", part.Id, "ok");
        Assert.Equal(ChecklistStatus.BadRequest, (await _catalog.SetTagsAsync("m", part.Id, "ok, no way")).Status);
        Assert.Equal(["ok"], (await _catalog.LoadGraphAsync()).TagsOf(part.Id).Select(t => t.Tag));
        Assert.Equal(ChecklistStatus.NotFound, (await _catalog.SetTagsAsync("m", Guid.NewGuid(), "x")).Status);
    }

    [Fact]
    public async Task A_locked_tag_cannot_be_removed_but_everything_else_can_and_adding_is_always_allowed()
    {
        var part = (await _catalog.CreateAsync("m", CatalogKind.Part, "P1", "Screw", null, null, tags: "a,b,c")).Value!;
        await _catalog.FreezeTagAsync(part.Id, "b");
        await _db.SaveChangesAsync();

        var refused = await _catalog.SetTagsAsync("m", part.Id, "a,c");
        Assert.Equal(ChecklistStatus.Conflict, refused.Status);
        Assert.Contains("'b'", refused.Message);
        Assert.Equal(["a", "b", "c"], (await _catalog.LoadGraphAsync()).TagsOf(part.Id).Select(t => t.Tag)); // nothing changed

        Assert.True((await _catalog.SetTagsAsync("m", part.Id, "b,d")).IsOk); // a and c removed, d added
        Assert.Equal(["b", "d"], (await _catalog.LoadGraphAsync()).TagsOf(part.Id).Select(t => t.Tag));
        Assert.NotNull((await _catalog.LoadGraphAsync()).TagsOf(part.Id).Single(t => t.Tag == "b").FrozenAt);
    }

    [Fact]
    public async Task Freezing_a_tag_the_item_does_not_have_does_nothing()
    {
        var part = await Part("P1");
        await _catalog.FreezeTagAsync(part.Id, "ghost");
        await _db.SaveChangesAsync();
        Assert.Empty(_db.CatalogTags);
    }

    [Fact]
    public async Task Search_by_tag_needs_all_the_tags_and_combines_with_the_other_filters()
    {
        await _catalog.CreateAsync("m", CatalogKind.Assembly, "A1", "Left", null, null, tags: "electrode-set,left");
        await _catalog.CreateAsync("m", CatalogKind.Assembly, "A2", "Right", null, null, tags: "electrode-set,right");
        await _catalog.CreateAsync("m", CatalogKind.Part, "P1", "Screw", null, null, tags: "screw");
        await Part("P2");

        Assert.Equal(["A1", "A2"], (await _catalog.SearchAsync(null, null, tags: ["electrode-set"])).Select(i => i.PartNo));
        Assert.Equal(["A1"], (await _catalog.SearchAsync(null, null, tags: ["electrode-set", "left"])).Select(i => i.PartNo));
        Assert.Empty(await _catalog.SearchAsync(null, null, tags: ["left", "right"]));
        Assert.Equal(["A2"], (await _catalog.SearchAsync(CatalogKind.Assembly, "right", tags: ["electrode-set"])).Select(i => i.PartNo));
        Assert.Empty(await _catalog.SearchAsync(CatalogKind.Part, null, tags: ["electrode-set"]));
        Assert.Equal(4, (await _catalog.SearchAsync(null, null, tags: [])).Count); // no tag filter: everything
    }

    [Fact]
    public async Task Tag_counts_list_the_tags_in_use_most_used_first()
    {
        await _catalog.CreateAsync("m", CatalogKind.Part, "P1", "x", null, null, tags: "screw,metal");
        await _catalog.CreateAsync("m", CatalogKind.Part, "P2", "x", null, null, tags: "screw");
        await _catalog.CreateAsync("m", CatalogKind.Part, "P3", "x", null, null, tags: "screw,abs");

        Assert.Equal([("screw", 3), ("abs", 1), ("metal", 1)], await _catalog.TagCountsAsync());
    }

    [Fact]
    public async Task Copy_as_new_copies_the_tags_unlocked()
    {
        var source = (await _catalog.CreateAsync("m", CatalogKind.Assembly, "A1", "Set", null, null, tags: "electrode-set,left")).Value!;
        await _catalog.FreezeTagAsync(source.Id, "electrode-set");
        await _db.SaveChangesAsync();

        var copy = (await _catalog.CopyAsNewAsync("m", source.Id, "A2", false)).Value!;
        var tags = (await _catalog.LoadGraphAsync()).TagsOf(copy.Id);
        Assert.Equal(["electrode-set", "left"], tags.Select(t => t.Tag));
        Assert.All(tags, t => Assert.Null(t.FrozenAt));
        Assert.True((await _catalog.SetTagsAsync("m", copy.Id, "left")).IsOk); // not locked on the copy
    }

    // ---- copy as new -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Copy_as_new_makes_a_draft_revision_with_the_same_contents_and_can_record_that_it_supersedes()
    {
        var screw = await Part("SCREW");
        var inner = await Assembly("INNER");
        await Add(inner, screw);
        var source = await Assembly("A1", "Terminal");
        await Add(source, inner, 4);
        await Add(source, await Part("CUP"), 1);
        var copy = (await _catalog.CopyAsNewAsync("m", source.Id, "A2", supersedesSource: true)).Value!;

        Assert.Equal("Terminal", copy.Name);
        Assert.Null(copy.FrozenAt); // a draft: it can be edited
        var graph = await _catalog.LoadGraphAsync();
        Assert.Equal(["INNER", "CUP"], graph.ChildrenOf(copy.Id).Select(c => graph.Items[c.ChildId].PartNo));
        Assert.Equal(4, graph.ChildrenOf(copy.Id)[0].Count);
        Assert.Equal(["A1"], graph.Supersedes(copy.Id).Select(i => i.PartNo));
        Assert.NotNull((await _catalog.GetAsync(inner.Id))!.FrozenAt);

        // Independent of the source: change the copy, the source is untouched.
        await _catalog.SetChildCountAsync("m", copy.Id, inner.Id, 5);
        Assert.Equal(4, (await _catalog.LoadGraphAsync()).ChildrenOf(source.Id)[0].Count);
    }

    [Fact]
    public async Task Copy_as_new_works_from_a_frozen_or_retired_source_and_needs_a_new_number()
    {
        var part = await Part("P1");
        var source = await Assembly("A1");
        await Add(source, part);
        await Add(await Assembly("PARENT"), source); // frozen
        await _catalog.RetireAsync("m", source.Id);

        Assert.True((await _catalog.CopyAsNewAsync("m", source.Id, "A2", false)).IsOk);
        Assert.Equal(ChecklistStatus.Conflict, (await _catalog.CopyAsNewAsync("m", source.Id, "a1", false)).Status);
        Assert.Equal(ChecklistStatus.BadRequest, (await _catalog.CopyAsNewAsync("m", source.Id, "bad number", false)).Status);
        Assert.Empty((await _catalog.LoadGraphAsync()).Supersedes((await _catalog.FindAsync("A2"))!.Id)); // not asked for
    }

    // ---- usage -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Units_carrying_a_part_are_found_through_nested_assemblies()
    {
        var screw = await Part("SCREW");
        var other = await Part("OTHER");
        var inner = await Assembly("INNER");
        await Add(inner, screw);
        var outer = await Assembly("OUTER");
        await Add(outer, inner);
        var unrelated = await Assembly("UNRELATED");
        await Add(unrelated, other);

        var unitA = new BciDevice { SerialNumber = "MSV2G0000011", DeviceType = "msv2" };
        var unitB = new BciDevice { SerialNumber = "MSV2G0000029", DeviceType = "msv2" };
        _db.BciDevices.AddRange(unitA, unitB);
        _db.SaveChanges();
        _db.ProductionSteps.Add(new ProductionStep { BciDeviceId = unitA.Id, Step = ProductionStepType.ElectrodesAssigned, PerformedAt = _clock.GetUtcNow(), AssemblyId = outer.Id });
        _db.ProductionSteps.Add(new ProductionStep { BciDeviceId = unitB.Id, Step = ProductionStepType.MountingAssigned, PerformedAt = _clock.GetUtcNow(), AssemblyId = unrelated.Id });
        _db.SaveChanges();

        var carrying = await _catalog.UnitsCarryingAsync(screw.Id);
        var use = Assert.Single(carrying);
        Assert.Equal(("MSV2G0000011", ProductionStepType.ElectrodesAssigned, "OUTER"), (use.SerialNumber, use.Step, use.Assembly.PartNo));
        Assert.Single(await _catalog.UnitsCarryingAsync(outer.Id)); // the assembly itself counts
        Assert.Empty(await _catalog.UnitsCarryingAsync(await Part("UNUSED") is var unused ? unused.Id : Guid.Empty));
    }

    [Fact]
    public async Task Search_matches_number_name_and_description_ignoring_case_and_filters_by_kind()
    {
        var p = (await _catalog.CreateAsync("m", CatalogKind.Part, "10002", "FrontPlateL", "front plate with \"L\" marking", null)).Value!;
        await Part("50002", "Electrode");
        await Assembly("10005", "Terminal");

        Assert.Equal(["10002"], (await _catalog.SearchAsync(null, "frontplate")).Select(i => i.PartNo));
        Assert.Equal(["10002"], (await _catalog.SearchAsync(null, "MARKING")).Select(i => i.PartNo));
        Assert.Equal(["10002", "10005"], (await _catalog.SearchAsync(null, "100")).Select(i => i.PartNo));
        Assert.Equal(["10005"], (await _catalog.SearchAsync(CatalogKind.Assembly, "100")).Select(i => i.PartNo));
        await _catalog.RetireAsync("m", p.Id);
        Assert.Equal(["10005"], (await _catalog.SearchAsync(null, "100", includeRetired: false)).Select(i => i.PartNo));
    }
}
