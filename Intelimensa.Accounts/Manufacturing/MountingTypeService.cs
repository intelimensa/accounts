using System.Text;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Manufacturing;

/// <summary>Shared checks for the free text Manufacturers type into the production lists.</summary>
internal static class ProductionText
{
    /// <summary>An error message, or null. A required value must be non-blank; no control characters at all.</summary>
    public static string? Check(string label, string? value, int max, bool required)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0)
            return required ? $"{label} is required." : null;
        if (text.Length > max)
            return $"{label} can be at most {max} characters.";
        return text.Any(char.IsControl) ? $"{label} can't contain control characters." : null;
    }

    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// The list of mounting clip types a unit can carry (production step 5). Manufacturers add and rename
/// entries and retire them; nothing is ever deleted, and a retired type stays on the units that carry it
/// but can't be picked again.
/// </summary>
public class MountingTypeService(ApplicationDbContext db, TimeProvider clock)
{
    public const int MaxNameLength = 100;

    public Task<List<MountingType>> ListAsync() =>
        db.MountingTypes.OrderBy(m => m.RetiredAt != null).ThenBy(m => m.DisplayName).ToListAsync();

    /// <summary>How many units carry each mounting type, by id.</summary>
    public async Task<Dictionary<int, int>> UnitCountsAsync() =>
        await db.ProductionSteps.Where(s => s.MountingTypeId != null)
            .GroupBy(s => s.MountingTypeId!.Value).Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N);

    public async Task<ChecklistResult<MountingType>> AddAsync(string? displayName)
    {
        if (ProductionText.Check("Name", displayName, MaxNameLength, required: true) is { } error)
            return new(ChecklistStatus.BadRequest, null, error);
        var name = displayName!.Trim();
        if (await NameTakenAsync(name, exceptId: null))
            return new(ChecklistStatus.Conflict, null, "A mounting type with that name already exists.");

        var type = new MountingType { Key = await UniqueKeyAsync(name), DisplayName = name };
        db.MountingTypes.Add(type);
        await db.SaveChangesAsync();
        return new(ChecklistStatus.Ok, type);
    }

    public async Task<ChecklistResult<MountingType>> RenameAsync(int id, string? displayName)
    {
        var type = await db.MountingTypes.FindAsync(id);
        if (type is null)
            return new(ChecklistStatus.NotFound, null, "Unknown mounting type.");
        if (ProductionText.Check("Name", displayName, MaxNameLength, required: true) is { } error)
            return new(ChecklistStatus.BadRequest, null, error);
        var name = displayName!.Trim();
        if (await NameTakenAsync(name, exceptId: id))
            return new(ChecklistStatus.Conflict, null, "A mounting type with that name already exists.");

        type.DisplayName = name; // the Key stays: it identifies the type, the name is only its label
        await db.SaveChangesAsync();
        return new(ChecklistStatus.Ok, type);
    }

    public async Task<ChecklistResult<MountingType>> RetireAsync(string userId, int id)
    {
        var type = await db.MountingTypes.FindAsync(id);
        if (type is null)
            return new(ChecklistStatus.NotFound, null, "Unknown mounting type.");
        if (type.RetiredAt is null)
        {
            type.RetiredAt = clock.GetUtcNow();
            type.RetiredByUserId = userId;
            await db.SaveChangesAsync();
        }
        return new(ChecklistStatus.Ok, type);
    }

    private Task<bool> NameTakenAsync(string name, int? exceptId)
    {
        var lowered = name.ToLowerInvariant();
        return db.MountingTypes.AnyAsync(m => m.Id != exceptId && m.DisplayName.ToLower() == lowered);
    }

    private async Task<string> UniqueKeyAsync(string name)
    {
        var slug = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
                slug.Append(c);
            else if (slug.Length > 0 && slug[^1] != '-')
                slug.Append('-');
        }
        var baseKey = slug.ToString().Trim('-');
        if (baseKey.Length == 0)
            baseKey = "mounting";
        if (baseKey.Length > 60)
            baseKey = baseKey[..60].Trim('-');

        var key = baseKey;
        for (var n = 2; await db.MountingTypes.AnyAsync(m => m.Key == key); n++)
            key = $"{baseKey}-{n}";
        return key;
    }
}
