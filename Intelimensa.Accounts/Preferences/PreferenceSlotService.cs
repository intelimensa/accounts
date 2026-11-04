using System.Text.Json;
using Intelimensa.Accounts.Api.Preferences;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Preferences;

public enum SlotOutcome
{
    Ok,

    /// <summary>The caller's version isn't the slot's: <see cref="SlotResult.Slot"/> is the slot as it is now.</summary>
    Conflict,

    /// <summary>The name or payload broke a limit: <see cref="SlotResult.Error"/> says which.</summary>
    Invalid,
}

public record SlotResult(SlotOutcome Outcome, PreferenceSlotResponse? Slot = null, string? Error = null);

/// <summary>
/// The five preference slots of an account. Writes are optimistic: the caller names the version it last read
/// (0 = the slot has no row yet) and loses with <see cref="SlotOutcome.Conflict"/> if someone else wrote since.
/// Callers have already checked the slot number with <see cref="PreferenceLimits.IsSlot"/>.
/// </summary>
public class PreferenceSlotService(ApplicationDbContext db, TimeProvider? clock = null)
{
    private static readonly string EmptyPayload = PreferenceLimits.Canonical(new PreferencePayload(1, []));

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<List<PreferenceSlotSummary>> ListAsync(int accountId)
    {
        var rows = await db.PreferenceSlots.AsNoTracking().Where(s => s.AccountId == accountId).ToListAsync();
        return Enumerable.Range(1, PreferenceLimits.SlotCount).Select(n =>
        {
            var row = rows.FirstOrDefault(r => r.Slot == n);
            return row is null
                ? new PreferenceSlotSummary(n, PreferenceLimits.DefaultName(n), 0, null, true)
                : new PreferenceSlotSummary(n, row.Name, row.Version, row.UpdatedAt, Read(row).Payload.Values.Count == 0);
        }).ToList();
    }

    public async Task<PreferenceSlotResponse> GetAsync(int accountId, int slot) =>
        await db.PreferenceSlots.AsNoTracking().FirstOrDefaultAsync(s => s.AccountId == accountId && s.Slot == slot)
            is { } row ? Read(row) : Unwritten(slot);

    public async Task<SlotResult> PutAsync(int accountId, int slot, long expectedVersion, string? name, PreferencePayload? payload)
    {
        var normalized = PreferenceLimits.NormalizeName(name, out var error);
        error ??= PreferenceLimits.ValidatePayload(payload);
        if (error is not null)
            return new SlotResult(SlotOutcome.Invalid, Error: error);

        return await WriteAsync(accountId, slot, expectedVersion, normalized!, PreferenceLimits.Canonical(payload!));
    }

    /// <summary>Changes the name only; on a slot with no row it creates an empty one.</summary>
    public async Task<SlotResult> RenameAsync(int accountId, int slot, long expectedVersion, string? name)
    {
        var normalized = PreferenceLimits.NormalizeName(name, out var error);
        if (error is not null)
            return new SlotResult(SlotOutcome.Invalid, Error: error);

        var row = await db.PreferenceSlots.AsNoTracking().FirstOrDefaultAsync(s => s.AccountId == accountId && s.Slot == slot);
        return await WriteAsync(accountId, slot, expectedVersion, normalized!, row?.Payload ?? EmptyPayload);
    }

    /// <summary>Removes the slot's row (name and payload reset). A slot with no row is already deleted.</summary>
    public async Task<SlotResult> DeleteAsync(int accountId, int slot, long expectedVersion)
    {
        var row = await db.PreferenceSlots.FirstOrDefaultAsync(s => s.AccountId == accountId && s.Slot == slot);
        if (row is null)
            return new SlotResult(SlotOutcome.Ok);
        if (row.Version != expectedVersion)
            return new SlotResult(SlotOutcome.Conflict, Read(row));

        db.PreferenceSlots.Remove(row);
        try
        {
            await db.SaveChangesAsync();
            return new SlotResult(SlotOutcome.Ok);
        }
        catch (DbUpdateConcurrencyException)
        {
            return await ConflictAsync(accountId, slot);
        }
    }

    private async Task<SlotResult> WriteAsync(int accountId, int slot, long expectedVersion, string name, string payloadJson)
    {
        var row = await db.PreferenceSlots.FirstOrDefaultAsync(s => s.AccountId == accountId && s.Slot == slot);
        if (row is null)
        {
            if (expectedVersion != 0)
                return new SlotResult(SlotOutcome.Conflict, Unwritten(slot));

            row = new PreferenceSlot { AccountId = accountId, Slot = slot, Name = name, Payload = payloadJson, Version = 1, UpdatedAt = _clock.GetUtcNow() };
            db.PreferenceSlots.Add(row);
        }
        else
        {
            if (row.Version != expectedVersion)
                return new SlotResult(SlotOutcome.Conflict, Read(row));

            row.Name = name;
            row.Payload = payloadJson;
            row.Version = expectedVersion + 1;
            row.UpdatedAt = _clock.GetUtcNow();
        }

        try
        {
            await db.SaveChangesAsync();
            return new SlotResult(SlotOutcome.Ok, Read(row));
        }
        catch (Exception e) when (e is DbUpdateConcurrencyException or DbUpdateException)
        {
            // someone wrote between our read and our save (or created the same slot): report where it stands now
            return await ConflictAsync(accountId, slot);
        }
    }

    private async Task<SlotResult> ConflictAsync(int accountId, int slot)
    {
        db.ChangeTracker.Clear();
        return new SlotResult(SlotOutcome.Conflict, await GetAsync(accountId, slot));
    }

    private static PreferenceSlotResponse Unwritten(int slot) =>
        new(slot, PreferenceLimits.DefaultName(slot), 0, null, new PreferencePayload(1, []));

    private static PreferenceSlotResponse Read(PreferenceSlot row) =>
        new(row.Slot, row.Name, row.Version, row.UpdatedAt, JsonSerializer.Deserialize<PreferencePayload>(row.Payload)!);
}
