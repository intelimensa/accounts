using Intelimensa.Accounts.Api.Preferences;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Intelimensa.Accounts.Preferences;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Tests.Preferences;

public class PreferenceSlotTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ApplicationDbContext _db;
    private readonly PreferenceSlotService _slots;
    private readonly int _account;
    private readonly int _other;

    public PreferenceSlotTests()
    {
        _connection.Open();
        _db = NewContext();
        _db.Database.EnsureCreated();
        _slots = new PreferenceSlotService(_db);
        _account = AddAccount("a");
        _other = AddAccount("b");
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private ApplicationDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);

    private int AddAccount(string name)
    {
        _db.Users.Add(new ApplicationUser { Id = name, UserName = name });
        var account = new Account { UserId = name };
        _db.Accounts.Add(account);
        _db.SaveChanges();
        return account.Id;
    }

    private static PreferencePayload Payload(params (string Key, string Value)[] values) =>
        new(1, values.ToDictionary(v => v.Key, v => v.Value));

    // ---- reading ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_account_has_five_empty_default_named_slots_at_version_zero()
    {
        var list = await _slots.ListAsync(_account);

        Assert.Equal(PreferenceLimits.SlotCount, list.Count);
        Assert.All(list, s => Assert.True(s.Empty));
        Assert.All(list, s => Assert.Equal(0, s.Version));
        Assert.Equal(["Profile 1", "Profile 2", "Profile 3", "Profile 4", "Profile 5"], list.Select(s => s.Name));
        Assert.Equal([1, 2, 3, 4, 5], list.Select(s => s.Slot));
    }

    [Fact]
    public async Task Getting_a_slot_that_was_never_written_answers_empty_at_version_zero()
    {
        var slot = await _slots.GetAsync(_account, 3);

        Assert.Equal(0, slot.Version);
        Assert.Equal("Profile 3", slot.Name);
        Assert.Empty(slot.Payload.Values);
        Assert.Null(slot.UpdatedAt);
    }

    // ---- writing ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Creating_a_slot_needs_version_zero_and_starts_it_at_one()
    {
        var result = await _slots.PutAsync(_account, 1, 0, "Morning", Payload(("KeybindLeft", "A")));

        Assert.Equal(SlotOutcome.Ok, result.Outcome);
        Assert.Equal(1, result.Slot!.Version);
        Assert.Equal("Morning", result.Slot.Name);
        Assert.Equal("A", result.Slot.Payload.Values["KeybindLeft"]);
        Assert.NotNull(result.Slot.UpdatedAt);

        var list = await _slots.ListAsync(_account);
        Assert.False(list[0].Empty);
        Assert.True(list[1].Empty);
    }

    [Fact]
    public async Task Replacing_a_slot_needs_the_current_version_and_bumps_it()
    {
        await _slots.PutAsync(_account, 1, 0, "Morning", Payload(("KeybindLeft", "A"), ("KeybindUp", "W")));

        var result = await _slots.PutAsync(_account, 1, 1, "Morning", Payload(("KeybindLeft", "Q")));

        Assert.Equal(SlotOutcome.Ok, result.Outcome);
        Assert.Equal(2, result.Slot!.Version);
        // replace, not merge: KeybindUp is gone, i.e. back to the config's default
        Assert.Equal(["KeybindLeft"], result.Slot.Payload.Values.Keys);
    }

    [Fact]
    public async Task A_stale_version_loses_and_is_told_the_slot_as_it_is_now()
    {
        await _slots.PutAsync(_account, 1, 0, "Morning", Payload(("KeybindLeft", "A")));
        await _slots.PutAsync(_account, 1, 1, "Morning", Payload(("KeybindLeft", "B")));

        var result = await _slots.PutAsync(_account, 1, 1, "Mine", Payload(("KeybindLeft", "C")));

        Assert.Equal(SlotOutcome.Conflict, result.Outcome);
        Assert.Equal(2, result.Slot!.Version);
        Assert.Equal("B", result.Slot.Payload.Values["KeybindLeft"]);
        Assert.Equal("Morning", result.Slot.Name);
        Assert.Equal("B", (await _slots.GetAsync(_account, 1)).Payload.Values["KeybindLeft"]);
    }

    [Fact]
    public async Task Creating_over_an_existing_slot_conflicts()
    {
        await _slots.PutAsync(_account, 1, 0, "Morning", Payload());

        var result = await _slots.PutAsync(_account, 1, 0, "Again", Payload());

        Assert.Equal(SlotOutcome.Conflict, result.Outcome);
        Assert.Equal("Morning", result.Slot!.Name);
    }

    [Fact]
    public async Task Replacing_a_slot_that_has_no_row_conflicts_with_the_empty_slot()
    {
        var result = await _slots.PutAsync(_account, 2, 5, "Evening", Payload());

        Assert.Equal(SlotOutcome.Conflict, result.Outcome);
        Assert.Equal(0, result.Slot!.Version);
        Assert.Equal("Profile 2", result.Slot.Name);
    }

    [Fact]
    public async Task Two_writers_from_the_same_version_cannot_both_win()
    {
        await _slots.PutAsync(_account, 1, 0, "Morning", Payload(("KeybindLeft", "A")));

        // a second machine: its own context, same database
        await using var other = NewContext();
        var theirs = await new PreferenceSlotService(other).PutAsync(_account, 1, 1, "Morning", Payload(("KeybindLeft", "T")));
        var ours = await _slots.PutAsync(_account, 1, 1, "Morning", Payload(("KeybindLeft", "O")));

        Assert.Equal(SlotOutcome.Ok, theirs.Outcome);
        Assert.Equal(SlotOutcome.Conflict, ours.Outcome);
        Assert.Equal("T", ours.Slot!.Payload.Values["KeybindLeft"]);
    }

    [Fact]
    public async Task A_writer_that_read_before_a_concurrent_save_is_refused_by_the_database()
    {
        await _slots.PutAsync(_account, 1, 0, "Morning", Payload(("KeybindLeft", "A")));

        // our context has the row tracked at version 1; another machine saves version 2 behind its back
        var tracked = await _db.PreferenceSlots.SingleAsync(s => s.AccountId == _account && s.Slot == 1);
        await using (var other = NewContext())
            Assert.Equal(SlotOutcome.Ok, (await new PreferenceSlotService(other).PutAsync(_account, 1, 1, "Morning", Payload(("KeybindLeft", "T")))).Outcome);

        var result = await _slots.PutAsync(_account, 1, 1, "Morning", Payload(("KeybindLeft", "O")));

        Assert.Equal(SlotOutcome.Conflict, result.Outcome);
        Assert.Equal("T", result.Slot!.Payload.Values["KeybindLeft"]);
        Assert.NotNull(tracked);
    }

    [Fact]
    public async Task Accounts_cannot_see_each_others_slots()
    {
        await _slots.PutAsync(_account, 1, 0, "Mine", Payload(("KeybindLeft", "A")));

        Assert.Equal(0, (await _slots.GetAsync(_other, 1)).Version);
        Assert.True((await _slots.ListAsync(_other))[0].Empty);
        // and the same slot number is free for the other account
        Assert.Equal(SlotOutcome.Ok, (await _slots.PutAsync(_other, 1, 0, "Theirs", Payload())).Outcome);
    }

    [Fact]
    public async Task Equal_content_is_stored_as_equal_text_whatever_the_key_order()
    {
        await _slots.PutAsync(_account, 1, 0, "A", new PreferencePayload(1, new() { ["B"] = "2", ["A"] = "1" }));
        await _slots.PutAsync(_account, 2, 0, "A", new PreferencePayload(1, new() { ["A"] = "1", ["B"] = "2" }));

        var rows = await _db.PreferenceSlots.AsNoTracking().OrderBy(s => s.Slot).ToListAsync();
        Assert.Equal(rows[0].Payload, rows[1].Payload);
        Assert.Equal("""{"V":1,"Values":{"A":"1","B":"2"}}""", rows[0].Payload);
    }

    // ---- rename and delete --------------------------------------------------------------------------------------

    [Fact]
    public async Task Renaming_keeps_the_payload_and_bumps_the_version()
    {
        await _slots.PutAsync(_account, 1, 0, "Morning", Payload(("KeybindLeft", "A")));

        var result = await _slots.RenameAsync(_account, 1, 1, "  Evening ");

        Assert.Equal(SlotOutcome.Ok, result.Outcome);
        Assert.Equal("Evening", result.Slot!.Name);
        Assert.Equal(2, result.Slot.Version);
        Assert.Equal("A", result.Slot.Payload.Values["KeybindLeft"]);
    }

    [Fact]
    public async Task Renaming_a_slot_that_was_never_written_creates_it_empty()
    {
        var result = await _slots.RenameAsync(_account, 4, 0, "Guest");

        Assert.Equal(SlotOutcome.Ok, result.Outcome);
        Assert.Equal(1, result.Slot!.Version);
        Assert.Empty(result.Slot.Payload.Values);
        var listed = (await _slots.ListAsync(_account))[3];
        Assert.Equal("Guest", listed.Name);
        Assert.True(listed.Empty);
    }

    [Fact]
    public async Task Renaming_with_a_stale_version_conflicts()
    {
        await _slots.PutAsync(_account, 1, 0, "Morning", Payload());

        Assert.Equal(SlotOutcome.Conflict, (await _slots.RenameAsync(_account, 1, 0, "Evening")).Outcome);
    }

    [Fact]
    public async Task Deleting_removes_the_row_and_the_name_goes_back_to_the_default()
    {
        await _slots.PutAsync(_account, 1, 0, "Morning", Payload(("KeybindLeft", "A")));

        Assert.Equal(SlotOutcome.Ok, (await _slots.DeleteAsync(_account, 1, 1)).Outcome);

        var slot = await _slots.GetAsync(_account, 1);
        Assert.Equal(0, slot.Version);
        Assert.Equal("Profile 1", slot.Name);
        Assert.Empty(slot.Payload.Values);
    }

    [Fact]
    public async Task Deleting_with_a_stale_version_conflicts_and_keeps_the_slot()
    {
        await _slots.PutAsync(_account, 1, 0, "Morning", Payload());
        await _slots.PutAsync(_account, 1, 1, "Morning", Payload(("KeybindLeft", "A")));

        var result = await _slots.DeleteAsync(_account, 1, 1);

        Assert.Equal(SlotOutcome.Conflict, result.Outcome);
        Assert.Equal(2, (await _slots.GetAsync(_account, 1)).Version);
    }

    [Fact]
    public async Task Deleting_a_slot_that_has_no_row_succeeds()
    {
        Assert.Equal(SlotOutcome.Ok, (await _slots.DeleteAsync(_account, 5, 0)).Outcome);
    }

    [Fact]
    public async Task A_deleted_slot_can_be_created_again_from_version_zero()
    {
        await _slots.PutAsync(_account, 1, 0, "Morning", Payload());
        await _slots.DeleteAsync(_account, 1, 1);

        var result = await _slots.PutAsync(_account, 1, 0, "Again", Payload());

        Assert.Equal(SlotOutcome.Ok, result.Outcome);
        Assert.Equal(1, result.Slot!.Version);
    }

    [Fact]
    public async Task Deleting_the_account_deletes_its_slots()
    {
        await _slots.PutAsync(_account, 1, 0, "Morning", Payload());

        _db.Users.Remove(await _db.Users.SingleAsync(u => u.Id == "a"));
        await _db.SaveChangesAsync();

        Assert.Empty(await _db.PreferenceSlots.ToListAsync());
    }

    [Fact]
    public async Task The_database_refuses_a_slot_number_outside_one_to_five()
    {
        _db.PreferenceSlots.Add(new PreferenceSlot { AccountId = _account, Slot = 6, Name = "x", Payload = "{}", Version = 1 });

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    // ---- limits -------------------------------------------------------------------------------------------------

    public static TheoryData<string?> BadNames() =>
        [null, "", "   ", new string('x', PreferenceLimits.MaxNameLength + 1), "bad\nname", "tab\tname"];

    [Theory]
    [MemberData(nameof(BadNames))]
    public async Task A_bad_name_is_refused_and_nothing_is_written(string? name)
    {
        var result = await _slots.PutAsync(_account, 1, 0, name, Payload());

        Assert.Equal(SlotOutcome.Invalid, result.Outcome);
        Assert.NotEmpty(result.Error!);
        Assert.Equal(0, (await _slots.GetAsync(_account, 1)).Version);
        Assert.Equal(SlotOutcome.Invalid, (await _slots.RenameAsync(_account, 1, 0, name)).Outcome);
    }

    [Fact]
    public async Task A_name_of_exactly_the_longest_length_is_accepted()
    {
        var name = new string('x', PreferenceLimits.MaxNameLength);

        Assert.Equal(SlotOutcome.Ok, (await _slots.PutAsync(_account, 1, 0, name, Payload())).Outcome);
    }

    public static TheoryData<string, PreferencePayload?> BadPayloads()
    {
        var tooMany = Enumerable.Range(0, PreferenceLimits.MaxKeys + 1).ToDictionary(i => $"K{i}", _ => "v");
        // 40 keys of the longest value is over the payload cap although each value is within its own
        var tooBig = Enumerable.Range(0, 40).ToDictionary(i => $"K{i}", _ => new string('x', PreferenceLimits.MaxValueLength));
        return new()
        {
            { "no payload", null },
            { "no values", new PreferencePayload(1, null!) },
            { "version zero", new PreferencePayload(0, []) },
            { "negative version", new PreferencePayload(-1, []) },
            { "too many keys", new PreferencePayload(1, tooMany) },
            { "empty key", new PreferencePayload(1, new() { [""] = "v" }) },
            { "key too long", new PreferencePayload(1, new() { [new string('k', PreferenceLimits.MaxKeyLength + 1)] = "v" }) },
            { "control character in key", new PreferencePayload(1, new() { ["a\nb"] = "v" }) },
            { "null value", new PreferencePayload(1, new() { ["K"] = null! }) },
            { "value too long", new PreferencePayload(1, new() { ["K"] = new string('v', PreferenceLimits.MaxValueLength + 1) }) },
            { "newline in value", new PreferencePayload(1, new() { ["K"] = "line1\nline2" }) },
            { "payload over the size cap", new PreferencePayload(1, tooBig) },
        };
    }

    [Theory]
    [MemberData(nameof(BadPayloads))]
    public async Task A_bad_payload_is_refused_and_nothing_is_written(string why, PreferencePayload? payload)
    {
        var result = await _slots.PutAsync(_account, 1, 0, "Morning", payload);

        Assert.True(result.Outcome == SlotOutcome.Invalid, why);
        Assert.NotEmpty(result.Error!);
        Assert.Equal(0, (await _slots.GetAsync(_account, 1)).Version);
    }

    [Fact]
    public async Task Payloads_at_every_limit_are_accepted()
    {
        var atLimits = new Dictionary<string, string>
        {
            [new string('k', PreferenceLimits.MaxKeyLength)] = new string('v', PreferenceLimits.MaxValueLength),
            ["Unicode"] = "Größe ✓",
            ["Empty"] = "",
        };

        Assert.Equal(SlotOutcome.Ok, (await _slots.PutAsync(_account, 1, 0, "Morning", new PreferencePayload(1, atLimits))).Outcome);

        var many = Enumerable.Range(0, PreferenceLimits.MaxKeys).ToDictionary(i => $"K{i}", _ => "v");
        Assert.Equal(SlotOutcome.Ok, (await _slots.PutAsync(_account, 2, 0, "Morning", new PreferencePayload(1, many))).Outcome);
        Assert.Equal(PreferenceLimits.MaxKeys, (await _slots.GetAsync(_account, 2)).Payload.Values.Count);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    [InlineData(-1, false)]
    public void Only_slots_one_to_five_exist(int slot, bool exists) => Assert.Equal(exists, PreferenceLimits.IsSlot(slot));
}
