using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Manufacturing;
using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Pages.Manufacturing;

/// <summary>One electrode set: its assemblies and parts, editable until the set is frozen or retired.</summary>
public class ElectrodeSetModel(UnitProvisioning provisioning, ElectrodeSetService sets, ApplicationDbContext db)
    : ProductionPageModel(provisioning, db)
{
    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    public ElectrodeSet? Set { get; private set; }

    public int Units { get; private set; }

    public Dictionary<string, string> Emails { get; private set; } = [];

    public bool Editable => Set is { FrozenAt: null, RetiredAt: null };

    /// <summary>Assemblies of other sets that can be copied into this one.</summary>
    public List<(Guid Id, string Label)> CopySources { get; private set; } = [];

    public Task OnGetAsync() => LoadAsync();

    public async Task<IActionResult> OnPostSaveAsync(string? name, string? version, string? description) =>
        await RunAsync(userId => sets.UpdateAsync(userId, Id, name, version, description), "Saved.");

    public async Task<IActionResult> OnPostNewRevisionAsync(string? newVersion)
    {
        if (await UserIdAsync() is not { } userId)
            return Forbid();
        var result = await sets.NewRevisionAsync(userId, Id, newVersion);
        if (!result.IsOk)
            return await FailAsync(result.Message);
        StatusMessage = $"New revision {result.Value!.Version} created from the previous set. You can edit it now.";
        return RedirectToPage(new { id = result.Value.Id });
    }

    public async Task<IActionResult> OnPostRetireAsync() =>
        await RunAsync(userId => sets.RetireAsync(userId, Id), "Retired.");

    public async Task<IActionResult> OnPostAddAssemblyAsync(string? name, string? version, string? partNumber, string? description, int count) =>
        await RunAsync(userId => sets.AddAssemblyAsync(userId, Id, name, version, partNumber, description, count), "Assembly added.");

    public async Task<IActionResult> OnPostSaveAssemblyAsync(Guid assemblyId, string? name, string? version, string? partNumber, string? description, int count) =>
        await RunAsync(userId => sets.UpdateAssemblyAsync(userId, assemblyId, name, version, partNumber, description, count), "Assembly saved.");

    public async Task<IActionResult> OnPostRemoveAssemblyAsync(Guid assemblyId) =>
        await RunAsync(userId => sets.RemoveAssemblyAsync(userId, assemblyId), "Assembly removed.");

    public async Task<IActionResult> OnPostDuplicateAssemblyAsync(Guid assemblyId, string? newName)
    {
        if (await UserIdAsync() is not { } userId)
            return Forbid();
        var result = await sets.DuplicateAssemblyAsync(userId, assemblyId, newName);
        if (!result.IsOk)
            return await FailAsync(result.Message);
        StatusMessage = $"Duplicated as {result.Value!.Name}. Change the parts that differ.";
        return RedirectToPage(pageName: null, pageHandler: null, routeValues: new { id = Id }, fragment: $"a-{result.Value.Id}");
    }

    public async Task<IActionResult> OnPostCopyAssemblyAsync(Guid sourceAssemblyId) =>
        await RunAsync(userId => sets.CopyAssemblyAsync(userId, Id, sourceAssemblyId), "Assembly copied.");

    public async Task<IActionResult> OnPostAddPartAsync(Guid assemblyId, string? name, string? version, string? partNumber, string? description, int count) =>
        await RunAsync(userId => sets.AddPartAsync(userId, assemblyId, name, version, partNumber, description, count), "Part added.");

    public async Task<IActionResult> OnPostSavePartAsync(Guid partId, string? name, string? version, string? partNumber, string? description, int count) =>
        await RunAsync(userId => sets.UpdatePartAsync(userId, partId, name, version, partNumber, description, count), "Part saved.");

    public async Task<IActionResult> OnPostRemovePartAsync(Guid partId) =>
        await RunAsync(userId => sets.RemovePartAsync(userId, partId), "Part removed.");

    private async Task<IActionResult> RunAsync<T>(Func<string, Task<ChecklistResult<T>>> action, string done)
    {
        if (await UserIdAsync() is not { } userId)
            return Forbid();
        var result = await action(userId);
        if (!result.IsOk)
            return await FailAsync(result.Message);
        StatusMessage = done;
        return RedirectToPage(new { id = Id });
    }

    protected override async Task LoadAsync()
    {
        Set = await sets.GetAsync(Id);
        if (Set is null)
            return;
        Units = await sets.UnitCountAsync(Id);
        Emails = await EmailsAsync([Set.CreatedByUserId, Set.LastModifiedByUserId, Set.RetiredByUserId]);

        if (Editable)
        {
            var others = await Db.ElectrodeAssemblies.Include(a => a.ElectrodeSet).Where(a => a.ElectrodeSetId != Id).ToListAsync();
            CopySources = others
                .OrderBy(a => a.ElectrodeSet!.Name, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.ElectrodeSet!.Version).ThenBy(a => a.Position)
                .Select(a => (a.Id, $"{a.ElectrodeSet!.Name} {a.ElectrodeSet.Version}: {a.Name} {a.Version}")).ToList();
        }
    }
}
