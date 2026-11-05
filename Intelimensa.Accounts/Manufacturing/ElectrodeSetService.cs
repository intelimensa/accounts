using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Manufacturing;

/// <summary>
/// Electrode sets (docs/production-checklist.md, section 6): a set holds assemblies, each assembly holds
/// parts, each with a count. Manufacturers define them freely. A set that has been assigned to a unit is
/// frozen: its contents can't change, and a new revision (a copy) is made instead. A set is never deleted,
/// only retired. Every change to a set's contents stamps who and when on the set.
/// </summary>
public class ElectrodeSetService(ApplicationDbContext db, TimeProvider clock)
{
    public const int MaxNameLength = 100;
    public const int MaxVersionLength = 32;
    public const int MaxPartNumberLength = 64;
    public const int MaxSetDescriptionLength = 1000;
    public const int MaxLineDescriptionLength = 500;
    public const int MaxAssemblies = 20;
    public const int MaxParts = 100;
    public const int MaxAssemblyCount = 99;
    public const int MaxPartCount = 999;

    public record SetRow(ElectrodeSet Set, int Units, int Assemblies);

    /// <summary>All sets (active first, then retired), with how many units carry each.</summary>
    public async Task<List<SetRow>> ListAsync()
    {
        var sets = await db.ElectrodeSets.Include(s => s.Assemblies).ToListAsync();
        var units = await db.ProductionSteps.Where(s => s.ElectrodeSetId != null)
            .GroupBy(s => s.ElectrodeSetId!.Value).Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N);
        return sets
            .OrderBy(s => s.RetiredAt != null).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Version, StringComparer.OrdinalIgnoreCase)
            .Select(s => new SetRow(s, units.GetValueOrDefault(s.Id), s.Assemblies.Count))
            .ToList();
    }

    public Task<ElectrodeSet?> GetAsync(Guid id) =>
        db.ElectrodeSets.Include(s => s.Assemblies.OrderBy(a => a.Position)).ThenInclude(a => a.Parts.OrderBy(p => p.Position))
            .AsSplitQuery().FirstOrDefaultAsync(s => s.Id == id);

    public Task<int> UnitCountAsync(Guid id) => db.ProductionSteps.CountAsync(s => s.ElectrodeSetId == id);

    // ---- the set ---------------------------------------------------------------------------------------------------

    public async Task<ChecklistResult<ElectrodeSet>> CreateAsync(string userId, string? name, string? version, string? description)
    {
        if (CheckHeader(name, version, description) is { } error)
            return new(ChecklistStatus.BadRequest, null, error);
        if (await SetTakenAsync(name!.Trim(), version!.Trim(), exceptId: null))
            return new(ChecklistStatus.Conflict, null, "A set with that name and version already exists.");

        var now = clock.GetUtcNow();
        var set = new ElectrodeSet
        {
            Name = name.Trim(), Version = version.Trim(), Description = ProductionText.Clean(description),
            CreatedAt = now, CreatedByUserId = userId, LastModifiedAt = now, LastModifiedByUserId = userId,
        };
        db.ElectrodeSets.Add(set);
        await db.SaveChangesAsync();
        return new(ChecklistStatus.Ok, set);
    }

    public async Task<ChecklistResult<ElectrodeSet>> UpdateAsync(string userId, Guid id, string? name, string? version, string? description)
    {
        var (set, failure) = await LoadEditableAsync(id);
        if (failure is not null)
            return new(failure.Status, null, failure.Message);
        if (CheckHeader(name, version, description) is { } error)
            return new(ChecklistStatus.BadRequest, null, error);
        if (await SetTakenAsync(name!.Trim(), version!.Trim(), exceptId: id))
            return new(ChecklistStatus.Conflict, null, "A set with that name and version already exists.");

        set!.Name = name.Trim();
        set.Version = version.Trim();
        set.Description = ProductionText.Clean(description);
        Stamp(set, userId);
        await db.SaveChangesAsync();
        return new(ChecklistStatus.Ok, set);
    }

    /// <summary>
    /// A new, editable set copied from <paramref name="id"/> (frozen or not): same name, all its assemblies and
    /// parts, the version you give (it must make the name+version pair new), pointing back at its source.
    /// </summary>
    public async Task<ChecklistResult<ElectrodeSet>> NewRevisionAsync(string userId, Guid id, string? newVersion)
    {
        var source = await GetAsync(id);
        if (source is null)
            return new(ChecklistStatus.NotFound, null, "Unknown electrode set.");
        if (ProductionText.Check("Version", newVersion, MaxVersionLength, required: true) is { } error)
            return new(ChecklistStatus.BadRequest, null, error);
        var version = newVersion!.Trim();
        if (await SetTakenAsync(source.Name, version, exceptId: null))
            return new(ChecklistStatus.Conflict, null, "A set with that name and version already exists; use another version.");

        var now = clock.GetUtcNow();
        var copy = new ElectrodeSet
        {
            Name = source.Name, Version = version, Description = source.Description, PreviousId = source.Id,
            CreatedAt = now, CreatedByUserId = userId, LastModifiedAt = now, LastModifiedByUserId = userId,
            Assemblies = source.Assemblies.Select(CopyAssembly).ToList(),
        };
        db.ElectrodeSets.Add(copy);
        await db.SaveChangesAsync();
        return new(ChecklistStatus.Ok, copy);
    }

    public async Task<ChecklistResult<ElectrodeSet>> RetireAsync(string userId, Guid id)
    {
        var set = await db.ElectrodeSets.FindAsync(id);
        if (set is null)
            return new(ChecklistStatus.NotFound, null, "Unknown electrode set.");
        if (set.RetiredAt is null)
        {
            set.RetiredAt = clock.GetUtcNow();
            set.RetiredByUserId = userId;
            await db.SaveChangesAsync();
        }
        return new(ChecklistStatus.Ok, set);
    }

    // ---- assemblies ------------------------------------------------------------------------------------------------

    public async Task<ChecklistResult<ElectrodeAssembly>> AddAssemblyAsync(
        string userId, Guid setId, string? name, string? version, string? partNumber, string? description, int count)
    {
        var (set, failure) = await LoadEditableAsync(setId);
        if (failure is not null)
            return new(failure.Status, null, failure.Message);
        if (CheckLine("Assembly", name, version, partNumber, description, count, MaxAssemblyCount) is { } error)
            return new(ChecklistStatus.BadRequest, null, error);

        var existing = await db.ElectrodeAssemblies.Where(a => a.ElectrodeSetId == setId).ToListAsync();
        if (existing.Count >= MaxAssemblies)
            return new(ChecklistStatus.BadRequest, null, $"A set can have at most {MaxAssemblies} assemblies.");
        if (existing.Any(a => Same(a.Name, a.Version, name!, version!)))
            return new(ChecklistStatus.Conflict, null, "This set already has an assembly with that name and version.");

        var assembly = new ElectrodeAssembly
        {
            ElectrodeSetId = setId, Position = existing.Count == 0 ? 1 : existing.Max(a => a.Position) + 1,
            Name = name!.Trim(), Version = version!.Trim(), PartNumber = ProductionText.Clean(partNumber),
            Description = ProductionText.Clean(description), Count = count,
        };
        db.ElectrodeAssemblies.Add(assembly);
        Stamp(set!, userId);
        await db.SaveChangesAsync();
        return new(ChecklistStatus.Ok, assembly);
    }

    public async Task<ChecklistResult<ElectrodeAssembly>> UpdateAssemblyAsync(
        string userId, Guid assemblyId, string? name, string? version, string? partNumber, string? description, int count)
    {
        var assembly = await db.ElectrodeAssemblies.FindAsync(assemblyId);
        if (assembly is null)
            return new(ChecklistStatus.NotFound, null, "Unknown assembly.");
        var (set, failure) = await LoadEditableAsync(assembly.ElectrodeSetId);
        if (failure is not null)
            return new(failure.Status, null, failure.Message);
        if (CheckLine("Assembly", name, version, partNumber, description, count, MaxAssemblyCount) is { } error)
            return new(ChecklistStatus.BadRequest, null, error);

        var siblings = await db.ElectrodeAssemblies.Where(a => a.ElectrodeSetId == assembly.ElectrodeSetId && a.Id != assemblyId).ToListAsync();
        if (siblings.Any(a => Same(a.Name, a.Version, name!, version!)))
            return new(ChecklistStatus.Conflict, null, "This set already has an assembly with that name and version.");

        assembly.Name = name!.Trim();
        assembly.Version = version!.Trim();
        assembly.PartNumber = ProductionText.Clean(partNumber);
        assembly.Description = ProductionText.Clean(description);
        assembly.Count = count;
        Stamp(set!, userId);
        await db.SaveChangesAsync();
        return new(ChecklistStatus.Ok, assembly);
    }

    /// <summary>Removes an assembly (and its parts) from a set that is still editable. Not a deletion of a set.</summary>
    public async Task<ChecklistResult<ElectrodeAssembly>> RemoveAssemblyAsync(string userId, Guid assemblyId)
    {
        var assembly = await db.ElectrodeAssemblies.FindAsync(assemblyId);
        if (assembly is null)
            return new(ChecklistStatus.NotFound, null, "Unknown assembly.");
        var (set, failure) = await LoadEditableAsync(assembly.ElectrodeSetId);
        if (failure is not null)
            return new(failure.Status, null, failure.Message);

        db.ElectrodeAssemblies.Remove(assembly);
        Stamp(set!, userId);
        await db.SaveChangesAsync();
        return new(ChecklistStatus.Ok, assembly);
    }

    /// <summary>
    /// Copies an assembly, with its parts, from any set into an editable one (a copy, never a link). Fails if the
    /// target already has an assembly of that name and version.
    /// </summary>
    public async Task<ChecklistResult<ElectrodeAssembly>> CopyAssemblyAsync(string userId, Guid targetSetId, Guid sourceAssemblyId)
    {
        var (set, failure) = await LoadEditableAsync(targetSetId);
        if (failure is not null)
            return new(failure.Status, null, failure.Message);
        var source = await db.ElectrodeAssemblies.Include(a => a.Parts).FirstOrDefaultAsync(a => a.Id == sourceAssemblyId);
        if (source is null)
            return new(ChecklistStatus.NotFound, null, "Unknown assembly.");

        var existing = await db.ElectrodeAssemblies.Where(a => a.ElectrodeSetId == targetSetId).ToListAsync();
        if (existing.Count >= MaxAssemblies)
            return new(ChecklistStatus.BadRequest, null, $"A set can have at most {MaxAssemblies} assemblies.");
        if (existing.Any(a => Same(a.Name, a.Version, source.Name, source.Version)))
            return new(ChecklistStatus.Conflict, null, "This set already has an assembly with that name and version.");

        var copy = CopyAssembly(source);
        copy.ElectrodeSetId = targetSetId;
        copy.Position = existing.Count == 0 ? 1 : existing.Max(a => a.Position) + 1;
        db.ElectrodeAssemblies.Add(copy);
        Stamp(set!, userId);
        await db.SaveChangesAsync();
        return new(ChecklistStatus.Ok, copy);
    }

    /// <summary>
    /// Duplicates an assembly, with all its parts, inside its own (editable) set under a new name: the way to make
    /// the right-hand bundle from the left-hand one and then change the parts that differ. The version is kept
    /// unless one is given; the name + version pair must be new within the set.
    /// </summary>
    public async Task<ChecklistResult<ElectrodeAssembly>> DuplicateAssemblyAsync(
        string userId, Guid assemblyId, string? newName, string? newVersion = null)
    {
        var source = await db.ElectrodeAssemblies.Include(a => a.Parts).FirstOrDefaultAsync(a => a.Id == assemblyId);
        if (source is null)
            return new(ChecklistStatus.NotFound, null, "Unknown assembly.");
        var (set, failure) = await LoadEditableAsync(source.ElectrodeSetId);
        if (failure is not null)
            return new(failure.Status, null, failure.Message);
        if (ProductionText.Check("Assembly name", newName, MaxNameLength, required: true) is { } nameError)
            return new(ChecklistStatus.BadRequest, null, nameError);
        var version = string.IsNullOrWhiteSpace(newVersion) ? source.Version : newVersion.Trim();
        if (ProductionText.Check("Assembly version", version, MaxVersionLength, required: true) is { } versionError)
            return new(ChecklistStatus.BadRequest, null, versionError);

        var siblings = await db.ElectrodeAssemblies.Where(a => a.ElectrodeSetId == source.ElectrodeSetId).ToListAsync();
        if (siblings.Count >= MaxAssemblies)
            return new(ChecklistStatus.BadRequest, null, $"A set can have at most {MaxAssemblies} assemblies.");
        if (siblings.Any(a => Same(a.Name, a.Version, newName!, version)))
            return new(ChecklistStatus.Conflict, null, "This set already has an assembly with that name and version; use another name.");

        var copy = CopyAssembly(source);
        copy.Name = newName!.Trim();
        copy.Version = version;
        copy.ElectrodeSetId = source.ElectrodeSetId;
        copy.Position = siblings.Max(a => a.Position) + 1;
        db.ElectrodeAssemblies.Add(copy);
        Stamp(set!, userId);
        await db.SaveChangesAsync();
        return new(ChecklistStatus.Ok, copy);
    }

    // ---- parts -----------------------------------------------------------------------------------------------------

    public async Task<ChecklistResult<ElectrodePart>> AddPartAsync(
        string userId, Guid assemblyId, string? name, string? version, string? partNumber, string? description, int count)
    {
        var assembly = await db.ElectrodeAssemblies.FindAsync(assemblyId);
        if (assembly is null)
            return new(ChecklistStatus.NotFound, null, "Unknown assembly.");
        var (set, failure) = await LoadEditableAsync(assembly.ElectrodeSetId);
        if (failure is not null)
            return new(failure.Status, null, failure.Message);
        if (CheckLine("Part", name, version, partNumber, description, count, MaxPartCount) is { } error)
            return new(ChecklistStatus.BadRequest, null, error);

        var existing = await db.ElectrodeParts.Where(p => p.ElectrodeAssemblyId == assemblyId).ToListAsync();
        if (existing.Count >= MaxParts)
            return new(ChecklistStatus.BadRequest, null, $"An assembly can have at most {MaxParts} parts.");
        if (existing.Any(p => Same(p.Name, p.Version, name!, version!)))
            return new(ChecklistStatus.Conflict, null, "This assembly already has a part with that name and version.");

        var part = new ElectrodePart
        {
            ElectrodeAssemblyId = assemblyId, Position = existing.Count == 0 ? 1 : existing.Max(p => p.Position) + 1,
            Name = name!.Trim(), Version = version!.Trim(), PartNumber = ProductionText.Clean(partNumber),
            Description = ProductionText.Clean(description), Count = count,
        };
        db.ElectrodeParts.Add(part);
        Stamp(set!, userId);
        await db.SaveChangesAsync();
        return new(ChecklistStatus.Ok, part);
    }

    public async Task<ChecklistResult<ElectrodePart>> UpdatePartAsync(
        string userId, Guid partId, string? name, string? version, string? partNumber, string? description, int count)
    {
        var part = await db.ElectrodeParts.Include(p => p.ElectrodeAssembly).FirstOrDefaultAsync(p => p.Id == partId);
        if (part is null)
            return new(ChecklistStatus.NotFound, null, "Unknown part.");
        var (set, failure) = await LoadEditableAsync(part.ElectrodeAssembly!.ElectrodeSetId);
        if (failure is not null)
            return new(failure.Status, null, failure.Message);
        if (CheckLine("Part", name, version, partNumber, description, count, MaxPartCount) is { } error)
            return new(ChecklistStatus.BadRequest, null, error);

        var siblings = await db.ElectrodeParts.Where(p => p.ElectrodeAssemblyId == part.ElectrodeAssemblyId && p.Id != partId).ToListAsync();
        if (siblings.Any(p => Same(p.Name, p.Version, name!, version!)))
            return new(ChecklistStatus.Conflict, null, "This assembly already has a part with that name and version.");

        part.Name = name!.Trim();
        part.Version = version!.Trim();
        part.PartNumber = ProductionText.Clean(partNumber);
        part.Description = ProductionText.Clean(description);
        part.Count = count;
        Stamp(set!, userId);
        await db.SaveChangesAsync();
        return new(ChecklistStatus.Ok, part);
    }

    public async Task<ChecklistResult<ElectrodePart>> RemovePartAsync(string userId, Guid partId)
    {
        var part = await db.ElectrodeParts.Include(p => p.ElectrodeAssembly).FirstOrDefaultAsync(p => p.Id == partId);
        if (part is null)
            return new(ChecklistStatus.NotFound, null, "Unknown part.");
        var (set, failure) = await LoadEditableAsync(part.ElectrodeAssembly!.ElectrodeSetId);
        if (failure is not null)
            return new(failure.Status, null, failure.Message);

        db.ElectrodeParts.Remove(part);
        Stamp(set!, userId);
        await db.SaveChangesAsync();
        return new(ChecklistStatus.Ok, part);
    }

    // ---- internals -------------------------------------------------------------------------------------------------

    /// <summary>The set, if it exists and may still be edited (not frozen, not retired); otherwise why not.</summary>
    private async Task<(ElectrodeSet? Set, ChecklistResult<object>? Failure)> LoadEditableAsync(Guid id)
    {
        var set = await db.ElectrodeSets.FindAsync(id);
        if (set is null)
            return (null, new(ChecklistStatus.NotFound, null, "Unknown electrode set."));
        if (set.FrozenAt is not null)
            return (null, new(ChecklistStatus.Conflict, null,
                "This set has been assigned to a unit and is frozen. Make a new revision to change it."));
        if (set.RetiredAt is not null)
            return (null, new(ChecklistStatus.Conflict, null, "This set is retired and can't be edited."));
        return (set, null);
    }

    private void Stamp(ElectrodeSet set, string userId)
    {
        set.LastModifiedAt = clock.GetUtcNow();
        set.LastModifiedByUserId = userId;
    }

    private Task<bool> SetTakenAsync(string name, string version, Guid? exceptId)
    {
        var n = name.ToLower();
        var v = version.ToLower();
        return db.ElectrodeSets.AnyAsync(s => s.Id != exceptId && s.Name.ToLower() == n && s.Version.ToLower() == v);
    }

    private static bool Same(string name, string version, string otherName, string otherVersion) =>
        name.Equals(otherName.Trim(), StringComparison.OrdinalIgnoreCase)
        && version.Equals(otherVersion.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? CheckHeader(string? name, string? version, string? description) =>
        ProductionText.Check("Name", name, MaxNameLength, true)
        ?? ProductionText.Check("Version", version, MaxVersionLength, true)
        ?? ProductionText.Check("Description", description, MaxSetDescriptionLength, false);

    private static string? CheckLine(string what, string? name, string? version, string? partNumber, string? description, int count, int maxCount) =>
        ProductionText.Check($"{what} name", name, MaxNameLength, true)
        ?? ProductionText.Check($"{what} version", version, MaxVersionLength, true)
        ?? ProductionText.Check($"{what} part number", partNumber, MaxPartNumberLength, false)
        ?? ProductionText.Check($"{what} description", description, MaxLineDescriptionLength, false)
        ?? (count is < 1 || count > maxCount ? $"{what} count must be between 1 and {maxCount}." : null);

    /// <summary>A detached copy (new ids, no set) of an assembly and its parts.</summary>
    private static ElectrodeAssembly CopyAssembly(ElectrodeAssembly source) => new()
    {
        Position = source.Position, Name = source.Name, Description = source.Description, Version = source.Version,
        PartNumber = source.PartNumber, Count = source.Count,
        Parts = source.Parts.OrderBy(p => p.Position).Select(p => new ElectrodePart
        {
            Position = p.Position, Name = p.Name, Description = p.Description, Version = p.Version,
            PartNumber = p.PartNumber, Count = p.Count,
        }).ToList(),
    };
}
