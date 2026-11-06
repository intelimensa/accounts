using System.Text;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Manufacturing;
using Intelimensa.Accounts.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Tests.Manufacturing;

public class CatalogXmlTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ApplicationDbContext _db;
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
    private readonly CatalogService _catalog;
    private readonly CatalogXml _xml;

    public CatalogXmlTests()
    {
        _connection.Open();
        _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser { Id = "m", UserName = "m" });
        _db.SaveChanges();
        _catalog = new CatalogService(_db, _clock);
        _xml = new CatalogXml(_db, _catalog, _clock);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private const string Sample = """
        <Catalog Format="1">
          <Parts>
            <Part PartNo="50002" Name="Electrode" Description="Silver chloride electrode"/>
            <Part PartNo="10002" Name="FrontPlateL" Description="front plate with &quot;L&quot; marking" Reference="cad/10002.step"/>
            <Part PartNo="50031" Name="ElectrodePiston" Description="Cylinder-shaped receptacle" Supersedes="50001"/>
            <Part PartNo="50001" Name="OldPiston" Retired="true"/>
          </Parts>
          <Assemblies>
            <Assembly PartNo="10005" Name="ElectrodeTerminal" Description="Short electrode">
              <Part PartNo="50002"/>
              <Part PartNo="50031" Count="2"/>
            </Assembly>
            <Assembly PartNo="10000" Name="Electrode bundle">
              <Part PartNo="10002"/>
              <Assembly PartNo="10005" Count="4"/>
            </Assembly>
          </Assemblies>
        </Catalog>
        """;

    private async Task<ImportReport> Import(string xml, bool validateOnly = false) =>
        await _xml.ImportAsync("m", new MemoryStream(Encoding.UTF8.GetBytes(xml)), validateOnly);

    private async Task<CatalogItem> Item(string partNo) => (await _catalog.FindAsync(partNo))!;

    private static string Wrap(string parts = "", string assemblies = "") =>
        $"<Catalog Format=\"1\"><Parts>{parts}</Parts><Assemblies>{assemblies}</Assemblies></Catalog>";

    // ---- a good import ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_valid_file_creates_everything_with_its_contents_links_and_retirements()
    {
        var report = await Import(Sample);

        Assert.True(report.Ok, string.Join("; ", report.Issues));
        Assert.True(report.Applied);
        Assert.Equal((6, 0, 0), (report.Created, report.Updated, report.Unchanged));
        Assert.Equal(1, report.Retired);
        Assert.Equal(1, report.LinksAdded);

        var plate = await Item("10002");
        Assert.Equal("front plate with \"L\" marking", plate.Description);
        Assert.Equal("cad/10002.step", plate.Reference);
        Assert.Equal("m", plate.CreatedByUserId);

        var graph = await _catalog.LoadGraphAsync();
        var bundle = await Item("10000");
        Assert.Equal(["10002", "10005"], graph.ChildrenOf(bundle.Id).Select(c => graph.Items[c.ChildId].PartNo));
        Assert.Equal([1, 4], graph.ChildrenOf(bundle.Id).Select(c => c.Count));
        Assert.Equal(["50001"], graph.Supersedes((await Item("50031")).Id).Select(i => i.PartNo));
        Assert.NotNull((await Item("50001")).RetiredAt);
        // What an assembly lists is frozen; the top of the tree is still a draft.
        Assert.NotNull((await Item("10005")).FrozenAt);
        Assert.Null(bundle.FrozenAt);
    }

    [Fact]
    public async Task Importing_the_same_file_again_changes_nothing()
    {
        await Import(Sample);
        var before = await _xml.ExportAsync();
        var changes = _db.CatalogChanges.Count();

        var again = await Import(Sample);

        Assert.True(again.Ok);
        Assert.Equal((0, 0, 6), (again.Created, again.Updated, again.Unchanged));
        Assert.Equal((0, 0), (again.Retired, again.LinksAdded));
        Assert.Equal(before, await _xml.ExportAsync());
        Assert.Equal(changes, _db.CatalogChanges.Count());
    }

    [Fact]
    public async Task Validate_only_reports_what_would_happen_and_changes_nothing()
    {
        var report = await Import(Sample, validateOnly: true);
        Assert.True(report.Ok);
        Assert.False(report.Applied);
        Assert.Equal(6, report.Created);
        Assert.Empty(_db.CatalogItems);
    }

    [Fact]
    public async Task Part_numbers_match_ignoring_case_and_keep_the_catalogs_spelling()
    {
        await Import(Wrap(parts: "<Part PartNo=\"Ab-1\" Name=\"One\"/>"));
        var again = await Import(Wrap(parts: "<Part PartNo=\"aB-1\" Name=\"One\"/>"));
        Assert.True(again.Ok);
        Assert.Equal(1, again.Unchanged);
        Assert.Equal("Ab-1", Assert.Single(_db.CatalogItems).PartNo);
    }

    // ---- updating --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Existing_items_get_new_names_and_descriptions_with_history_and_a_draft_assemblys_contents_are_replaced()
    {
        await Import(Wrap(
            parts: "<Part PartNo=\"P1\" Name=\"Old\"/><Part PartNo=\"P2\" Name=\"Two\"/><Part PartNo=\"P3\" Name=\"Three\"/>",
            assemblies: "<Assembly PartNo=\"A1\" Name=\"Asm\"><Part PartNo=\"P1\"/><Part PartNo=\"P2\" Count=\"2\"/></Assembly>"));

        var report = await Import(Wrap(
            parts: "<Part PartNo=\"P1\" Name=\"New\" Description=\"described\"/><Part PartNo=\"P2\" Name=\"Two\"/><Part PartNo=\"P3\" Name=\"Three\"/>",
            assemblies: "<Assembly PartNo=\"A1\" Name=\"Asm\"><Part PartNo=\"P2\" Count=\"5\"/><Part PartNo=\"P3\"/></Assembly>"));

        Assert.True(report.Ok, string.Join("; ", report.Issues));
        Assert.Equal((0, 2, 2), (report.Created, report.Updated, report.Unchanged)); // P1 and A1 changed; P2 and P3 did not
        var p1 = await Item("P1");
        Assert.Equal(("New", "described"), (p1.Name, p1.Description));
        Assert.Equal(["Description", "Name"], (await _catalog.HistoryAsync(p1.Id)).Select(c => c.Field).Order());

        var graph = await _catalog.LoadGraphAsync();
        var lines = graph.ChildrenOf((await Item("A1")).Id);
        Assert.Equal(["P2", "P3"], lines.Select(l => graph.Items[l.ChildId].PartNo));
        Assert.Equal([5, 1], lines.Select(l => l.Count));
    }

    [Fact]
    public async Task A_frozen_assembly_may_be_listed_again_with_the_same_contents_in_any_order_but_not_changed()
    {
        await Import(Wrap(
            parts: "<Part PartNo=\"P1\" Name=\"One\"/><Part PartNo=\"P2\" Name=\"Two\"/>",
            assemblies: "<Assembly PartNo=\"A1\" Name=\"Asm\"><Part PartNo=\"P1\"/><Part PartNo=\"P2\"/></Assembly>"));
        await _catalog.FreezeAsync((await Item("A1")).Id);
        await _db.SaveChangesAsync();

        var reordered = await Import(Wrap(
            parts: "<Part PartNo=\"P1\" Name=\"One\"/><Part PartNo=\"P2\" Name=\"Two\"/>",
            assemblies: "<Assembly PartNo=\"A1\" Name=\"Renamed ok\"><Part PartNo=\"P2\"/><Part PartNo=\"P1\"/></Assembly>"));
        Assert.True(reordered.Ok, string.Join("; ", reordered.Issues));
        Assert.Equal("Renamed ok", (await Item("A1")).Name); // name edits are always allowed

        var changed = await Import(Wrap(
            parts: "<Part PartNo=\"P1\" Name=\"One\"/><Part PartNo=\"P2\" Name=\"Two\"/>",
            assemblies: "<Assembly PartNo=\"A1\" Name=\"Asm\"><Part PartNo=\"P1\" Count=\"2\"/><Part PartNo=\"P2\"/></Assembly>"));
        Assert.False(changed.Ok);
        Assert.Contains("frozen", changed.Issues.Single().Message);
        Assert.Contains("new part number", changed.Issues.Single().Message);
    }

    [Fact]
    public async Task Supersedes_are_added_never_removed_and_retiring_is_never_undone()
    {
        await Import(Wrap(parts: "<Part PartNo=\"P1\" Name=\"One\"/><Part PartNo=\"P2\" Name=\"Two\" Supersedes=\"P1\" Retired=\"true\"/>"));

        var again = await Import(Wrap(parts: "<Part PartNo=\"P1\" Name=\"One\"/><Part PartNo=\"P2\" Name=\"Two\"/>"));

        Assert.True(again.Ok);
        Assert.Single(_db.CatalogSupersessions);
        Assert.NotNull((await Item("P2")).RetiredAt);
    }

    [Fact]
    public async Task Items_missing_from_a_later_file_are_left_alone()
    {
        await Import(Sample);
        await Import(Wrap(parts: "<Part PartNo=\"NEW\" Name=\"New\"/>"));
        Assert.Equal(7, _db.CatalogItems.Count());
    }

    // ---- tags ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Tags_are_imported_normalized_and_exported_sorted_and_comma_separated()
    {
        var report = await Import(Wrap(
            parts: "<Part PartNo=\"P1\" Name=\"One\" Tags=\" Screw, METAL ,screw\"/><Part PartNo=\"P2\" Name=\"Two\"/>",
            assemblies: "<Assembly PartNo=\"A1\" Name=\"Set\" Tags=\"electrode-set\"><Part PartNo=\"P1\"/></Assembly>"));
        Assert.True(report.Ok, string.Join("; ", report.Issues));

        var graph = await _catalog.LoadGraphAsync();
        Assert.Equal(["metal", "screw"], graph.TagsOf((await Item("P1")).Id).Select(t => t.Tag));
        Assert.Empty(graph.TagsOf((await Item("P2")).Id));

        var text = Encoding.UTF8.GetString(await _xml.ExportAsync());
        Assert.Contains("Tags=\"metal,screw\"", text);
        Assert.Contains("Tags=\"electrode-set\"", text);
        Assert.Equal(2, text.Split("Tags=").Length - 1); // none written for an untagged item
    }

    [Fact]
    public async Task An_absent_tags_attribute_leaves_tags_alone_and_an_empty_one_clears_them()
    {
        await Import(Wrap(parts: "<Part PartNo=\"P1\" Name=\"One\" Tags=\"a,b\"/>"));

        var absent = await Import(Wrap(parts: "<Part PartNo=\"P1\" Name=\"One\"/>"));
        Assert.Equal((0, 0, 1), (absent.Created, absent.Updated, absent.Unchanged));
        Assert.Equal(2, (await _catalog.LoadGraphAsync()).TagsOf((await Item("P1")).Id).Count);

        var changed = await Import(Wrap(parts: "<Part PartNo=\"P1\" Name=\"One\" Tags=\"b,c\"/>"));
        Assert.Equal((0, 1, 0), (changed.Created, changed.Updated, changed.Unchanged));
        Assert.Equal(["b", "c"], (await _catalog.LoadGraphAsync()).TagsOf((await Item("P1")).Id).Select(t => t.Tag));

        var same = await Import(Wrap(parts: "<Part PartNo=\"P1\" Name=\"One\" Tags=\"c, B\"/>"));
        Assert.Equal(1, same.Unchanged);

        var cleared = await Import(Wrap(parts: "<Part PartNo=\"P1\" Name=\"One\" Tags=\"\"/>"));
        Assert.Equal(1, cleared.Updated);
        Assert.Empty((await _catalog.LoadGraphAsync()).TagsOf((await Item("P1")).Id));
        Assert.Contains((await _catalog.HistoryAsync((await Item("P1")).Id)), c => c.Field == "Tags");
    }

    [Fact]
    public async Task Removing_a_locked_tag_by_import_is_refused_and_nothing_else_in_the_file_is_applied()
    {
        await Import(Wrap(parts: "<Part PartNo=\"P1\" Name=\"One\" Tags=\"keep,other\"/>"));
        await _catalog.FreezeTagAsync((await Item("P1")).Id, "keep");
        await _db.SaveChangesAsync();

        var refused = await Import(Wrap(parts: "<Part PartNo=\"P1\" Name=\"One\" Tags=\"other\"/><Part PartNo=\"P9\" Name=\"Innocent\"/>"));
        Assert.False(refused.Ok);
        Assert.Contains("locked", refused.Issues.Single().Message);
        Assert.Null(await _catalog.FindAsync("P9"));

        // Keeping it, or adding more, is fine.
        Assert.True((await Import(Wrap(parts: "<Part PartNo=\"P1\" Name=\"One\" Tags=\"keep,new\"/>"))).Ok);
    }

    [Fact]
    public async Task Validate_only_counts_a_tag_change_without_applying_it()
    {
        await Import(Wrap(parts: "<Part PartNo=\"P1\" Name=\"One\" Tags=\"a\"/>"));
        var dry = await Import(Wrap(parts: "<Part PartNo=\"P1\" Name=\"One\" Tags=\"a,b\"/>"), validateOnly: true);
        Assert.Equal((0, 1, 0), (dry.Created, dry.Updated, dry.Unchanged));
        Assert.Equal(["a"], (await _catalog.LoadGraphAsync()).TagsOf((await Item("P1")).Id).Select(t => t.Tag));
    }

    [Fact]
    public Task A_bad_tag_in_a_file_is_refused() =>
        AssertRefused(Wrap(parts: "<Part PartNo=\"P1\" Name=\"One\" Tags=\"fine, not fine\"/>"), "not a valid tag");

    [Fact]
    public async Task Tags_survive_an_export_and_import_into_an_empty_catalog()
    {
        await Import(Wrap(
            parts: "<Part PartNo=\"P1\" Name=\"One\" Tags=\"screw,m3\"/>",
            assemblies: "<Assembly PartNo=\"A1\" Name=\"Set\" Tags=\"electrode-set\"><Part PartNo=\"P1\"/></Assembly>"));
        var exported = await _xml.ExportAsync();

        using var other = new SqliteConnection("Data Source=:memory:");
        other.Open();
        await using var db2 = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(other).Options);
        db2.Database.EnsureCreated();
        db2.Users.Add(new ApplicationUser { Id = "m", UserName = "m" });
        db2.SaveChanges();
        var xml2 = new CatalogXml(db2, new CatalogService(db2, _clock), _clock);

        Assert.True((await xml2.ImportAsync("m", new MemoryStream(exported), false)).Ok);
        Assert.Equal(exported, await xml2.ExportAsync());
        Assert.Equal(3, db2.CatalogTags.Count());
    }

    // ---- refusals: nothing is applied when anything is wrong ------------------------------------------------------

    private async Task AssertRefused(string xml, string expected, int? line = null)
    {
        var report = await Import(xml);
        Assert.False(report.Ok);
        Assert.False(report.Applied);
        Assert.Contains(report.Issues, i => i.Message.Contains(expected, StringComparison.OrdinalIgnoreCase));
        if (line is not null)
            Assert.Contains(report.Issues, i => i.Line == line && i.Message.Contains(expected, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(_db.CatalogItems);
    }

    [Fact]
    public Task Malformed_xml_is_refused_with_the_line() =>
        AssertRefused("<Catalog Format=\"1\">\n<Parts>\n<Part PartNo=\"P1\" Name=\"One\">\n</Parts></Catalog>", "not well-formed", 4);

    [Fact]
    public Task A_backslash_escaped_quote_is_not_xml() =>
        AssertRefused("<Catalog Format=\"1\"><Parts><Part PartNo=\"P1\" Name=\"x\" Description=\"plate \\\"L\\\" marking\"/></Parts></Catalog>", "not well-formed");

    [Fact]
    public Task The_root_must_be_catalog() => AssertRefused("<Parts/>", "root element must be <Catalog>");

    [Fact]
    public Task The_format_version_is_required() => AssertRefused("<Catalog><Parts/></Catalog>", "Format=\"1\"");

    [Fact]
    public Task Another_format_version_is_refused() => AssertRefused("<Catalog Format=\"2\"><Parts/></Catalog>", "Format=\"1\"");

    [Fact]
    public Task Unknown_attributes_are_errors_so_typos_are_caught() =>
        AssertRefused(Wrap(parts: "<Part PartNo=\"P1\" Name=\"One\" Descripton=\"typo\"/>"), "unknown attribute 'Descripton'");

    [Fact]
    public Task Unknown_elements_are_errors() =>
        AssertRefused("<Catalog Format=\"1\"><Widgets/></Catalog>", "unknown element <Widgets>");

    [Fact]
    public Task A_reference_takes_only_a_part_number_and_a_count() =>
        AssertRefused(Wrap(
            parts: "<Part PartNo=\"P1\" Name=\"One\"/>",
            assemblies: "<Assembly PartNo=\"A1\" Name=\"Asm\"><Part PartNo=\"P1\" Name=\"Inline\"/></Assembly>"), "unknown attribute 'Name'");

    [Fact]
    public Task A_part_cannot_have_contents() =>
        AssertRefused(Wrap(parts: "<Part PartNo=\"P1\" Name=\"One\"><Part PartNo=\"P2\"/></Part>"), "a part has no contents");

    [Fact]
    public Task A_bad_part_number_is_refused() =>
        AssertRefused(Wrap(parts: "<Part PartNo=\"has space\" Name=\"One\"/>"), "not a valid part number");

    [Fact]
    public Task A_missing_name_is_refused() =>
        AssertRefused(Wrap(parts: "<Part PartNo=\"P1\"/>"), "name is required");

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("10000")]
    [InlineData("two")]
    [InlineData("1.5")]
    public Task A_bad_count_is_refused(string count) =>
        AssertRefused(Wrap(
            parts: "<Part PartNo=\"P1\" Name=\"One\"/>",
            assemblies: $"<Assembly PartNo=\"A1\" Name=\"Asm\"><Part PartNo=\"P1\" Count=\"{count}\"/></Assembly>"), "Count must be a whole number");

    [Fact]
    public Task A_part_number_defined_twice_is_refused() =>
        AssertRefused(Wrap(parts: "<Part PartNo=\"P1\" Name=\"One\"/><Part PartNo=\"p1\" Name=\"Again\"/>"), "defined twice");

    [Fact]
    public Task One_number_for_a_part_and_an_assembly_is_refused() =>
        AssertRefused(Wrap(parts: "<Part PartNo=\"X1\" Name=\"One\"/>", assemblies: "<Assembly PartNo=\"X1\" Name=\"Asm\"/>"), "defined twice");

    [Fact]
    public Task A_reference_to_nothing_is_refused() =>
        AssertRefused(Wrap(assemblies: "<Assembly PartNo=\"A1\" Name=\"Asm\"><Part PartNo=\"GHOST\"/></Assembly>"), "isn't in the file or the catalog");

    [Fact]
    public Task A_reference_must_use_the_kind_of_what_it_names() =>
        AssertRefused(Wrap(
            parts: "<Part PartNo=\"P1\" Name=\"One\"/>",
            assemblies: "<Assembly PartNo=\"A1\" Name=\"Asm\"><Assembly PartNo=\"P1\"/></Assembly>"), "list it with <Part>");

    [Fact]
    public Task The_same_child_twice_is_refused() =>
        AssertRefused(Wrap(
            parts: "<Part PartNo=\"P1\" Name=\"One\"/>",
            assemblies: "<Assembly PartNo=\"A1\" Name=\"Asm\"><Part PartNo=\"P1\"/><Part PartNo=\"p1\"/></Assembly>"), "twice; use Count");

    [Fact]
    public Task An_assembly_cannot_contain_itself_through_others() =>
        AssertRefused(Wrap(assemblies:
            "<Assembly PartNo=\"A\" Name=\"A\"><Assembly PartNo=\"B\"/></Assembly>"
            + "<Assembly PartNo=\"B\" Name=\"B\"><Assembly PartNo=\"C\"/></Assembly>"
            + "<Assembly PartNo=\"C\" Name=\"C\"><Assembly PartNo=\"A\"/></Assembly>"), "contain itself");

    [Fact]
    public Task An_assembly_cannot_contain_itself_directly() =>
        AssertRefused(Wrap(assemblies: "<Assembly PartNo=\"A\" Name=\"A\"><Assembly PartNo=\"A\"/></Assembly>"), "contain itself");

    [Fact]
    public Task Supersedes_must_name_something_that_exists_of_the_same_kind() =>
        AssertRefused(Wrap(parts: "<Part PartNo=\"P1\" Name=\"One\" Supersedes=\"NOPE\"/>"), "isn't in the file or the catalog");

    [Fact]
    public Task Supersedes_cannot_cross_kinds() =>
        AssertRefused(Wrap(
            parts: "<Part PartNo=\"P1\" Name=\"One\" Supersedes=\"A1\"/>",
            assemblies: "<Assembly PartNo=\"A1\" Name=\"Asm\"/>"), "not the same kind");

    [Fact]
    public Task Supersedes_cannot_form_a_cycle() =>
        AssertRefused(Wrap(parts:
            "<Part PartNo=\"P1\" Name=\"One\" Supersedes=\"P2\"/><Part PartNo=\"P2\" Name=\"Two\" Supersedes=\"P1\"/>"), "form a cycle");

    [Fact]
    public Task An_item_cannot_supersede_itself() =>
        AssertRefused(Wrap(parts: "<Part PartNo=\"P1\" Name=\"One\" Supersedes=\"P1\"/>"), "can't supersede itself");

    [Fact]
    public Task A_dtd_is_prohibited_so_there_is_no_entity_expansion_or_file_access() =>
        AssertRefused(
            "<?xml version=\"1.0\"?><!DOCTYPE Catalog [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><Catalog Format=\"1\"><Parts><Part PartNo=\"P1\" Name=\"&x;\"/></Parts></Catalog>",
            "not well-formed");

    [Fact]
    public async Task Every_problem_is_reported_not_just_the_first()
    {
        var report = await Import(Wrap(
            parts: "<Part PartNo=\"bad one\" Name=\"x\"/><Part PartNo=\"P2\"/><Part PartNo=\"P3\" Name=\"n\" Bogus=\"1\"/>"));
        Assert.True(report.Issues.Count >= 3);
        Assert.Empty(_db.CatalogItems);
    }

    [Fact]
    public async Task A_file_with_one_bad_item_applies_none_of_the_good_ones()
    {
        var report = await Import(Wrap(
            parts: "<Part PartNo=\"GOOD\" Name=\"Good\"/>",
            assemblies: "<Assembly PartNo=\"A1\" Name=\"Asm\"><Part PartNo=\"GHOST\"/></Assembly>"));
        Assert.False(report.Ok);
        Assert.Empty(_db.CatalogItems);
    }

    [Fact]
    public async Task A_kind_cannot_be_redefined_and_a_retired_part_cannot_be_newly_listed()
    {
        await Import(Wrap(parts: "<Part PartNo=\"P1\" Name=\"One\" Retired=\"true\"/>", assemblies: "<Assembly PartNo=\"A1\" Name=\"Asm\"/>"));

        var redefined = await Import(Wrap(assemblies: "<Assembly PartNo=\"P1\" Name=\"Now an assembly\"/>"));
        Assert.Contains(redefined.Issues, i => i.Message.Contains("can't be redefined"));

        var listing = await Import(Wrap(
            parts: "<Part PartNo=\"P1\" Name=\"One\"/>",
            assemblies: "<Assembly PartNo=\"A1\" Name=\"Asm\"><Part PartNo=\"P1\"/></Assembly>"));
        Assert.Contains(listing.Issues, i => i.Message.Contains("retired"));

        var fresh = await Import(Wrap(
            parts: "<Part PartNo=\"P1\" Name=\"One\"/>",
            assemblies: "<Assembly PartNo=\"NEW\" Name=\"New\"><Part PartNo=\"P1\"/></Assembly>"));
        Assert.Contains(fresh.Issues, i => i.Message.Contains("retired"));
    }

    [Fact]
    public async Task A_file_over_the_size_limit_is_refused()
    {
        var big = new MemoryStream(new byte[CatalogXml.MaxBytes + 1]);
        var report = await _xml.ImportAsync("m", big, validateOnly: false);
        Assert.False(report.Ok);
        Assert.Contains("larger than", report.Issues.Single().Message);
    }

    // ---- export ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Export_then_import_into_an_empty_catalog_reproduces_it()
    {
        await Import(Sample);
        var exported = await _xml.ExportAsync();

        // A fresh database.
        using var other = new SqliteConnection("Data Source=:memory:");
        other.Open();
        await using var db2 = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(other).Options);
        db2.Database.EnsureCreated();
        db2.Users.Add(new ApplicationUser { Id = "m", UserName = "m" });
        db2.SaveChanges();
        var catalog2 = new CatalogService(db2, _clock);
        var xml2 = new CatalogXml(db2, catalog2, _clock);

        var report = await xml2.ImportAsync("m", new MemoryStream(exported), validateOnly: false);

        Assert.True(report.Ok, string.Join("; ", report.Issues));
        Assert.Equal(exported, await xml2.ExportAsync());
    }

    [Fact]
    public async Task Export_is_well_formed_utf8_with_parts_first_and_children_before_parents_and_escapes_quotes()
    {
        await Import(Sample);
        var text = Encoding.UTF8.GetString(await _xml.ExportAsync());

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", text);
        Assert.Contains("Description=\"front plate with &quot;L&quot; marking\"", text);
        Assert.True(text.IndexOf("<Parts>") < text.IndexOf("<Assemblies>"));
        Assert.True(text.IndexOf("PartNo=\"10005\" Name=\"ElectrodeTerminal\"") < text.IndexOf("PartNo=\"10000\" Name=\"Electrode bundle\""));
        Assert.Contains("Count=\"4\"", text);
        Assert.DoesNotContain("Count=\"1\"", text); // the default is not written
        Assert.Contains("Retired=\"true\"", text);
        Assert.DoesNotContain("encoding=\"utf-16\"", text);
    }

    [Fact]
    public async Task Exporting_one_assembly_includes_everything_below_it_and_drops_supersedes_outside_the_export()
    {
        await Import(Sample);

        var text = Encoding.UTF8.GetString(await _xml.ExportAsync((await Item("10005")).Id));

        Assert.Contains("PartNo=\"10005\"", text);
        Assert.Contains("PartNo=\"50002\"", text);
        Assert.Contains("PartNo=\"50031\"", text);
        Assert.DoesNotContain("PartNo=\"10000\"", text); // the bundle above it is not part of the export
        Assert.DoesNotContain("PartNo=\"10002\"", text);
        Assert.DoesNotContain("PartNo=\"50001\"", text);  // the superseded part isn't below it
        Assert.DoesNotContain("Supersedes=", text);       // so the link to it is dropped

        // And it is a file that imports on its own.
        using var other = new SqliteConnection("Data Source=:memory:");
        other.Open();
        await using var db2 = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(other).Options);
        db2.Database.EnsureCreated();
        db2.Users.Add(new ApplicationUser { Id = "m", UserName = "m" });
        db2.SaveChanges();
        var xml2 = new CatalogXml(db2, new CatalogService(db2, _clock), _clock);
        var report = await xml2.ImportAsync("m", new MemoryStream(Encoding.UTF8.GetBytes(text)), false);
        Assert.True(report.Ok, string.Join("; ", report.Issues));
        Assert.Equal(3, db2.CatalogItems.Count()); // the terminal and its two parts
    }

    [Fact]
    public async Task Part_numbers_with_leading_zeros_survive_the_round_trip()
    {
        await Import(Wrap(parts: "<Part PartNo=\"00042\" Name=\"Zeros\"/>"));
        Assert.Contains("PartNo=\"00042\"", Encoding.UTF8.GetString(await _xml.ExportAsync()));
    }
}
