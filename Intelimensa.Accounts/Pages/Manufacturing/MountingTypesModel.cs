using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Manufacturing;
using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.Mvc;

namespace Intelimensa.Accounts.Pages.Manufacturing;

/// <summary>The list of mounting clip types (production step 5): add, rename, retire. Never deleted.</summary>
public class MountingTypesModel(UnitProvisioning provisioning, MountingTypeService mountings, ApplicationDbContext db)
    : ProductionPageModel(provisioning, db)
{
    public List<MountingType> Types { get; private set; } = [];

    public Dictionary<int, int> Units { get; private set; } = [];

    public Task OnGetAsync() => LoadAsync();

    public async Task<IActionResult> OnPostAddAsync(string? displayName)
    {
        if (await UserIdAsync() is null)
            return Forbid();
        var result = await mountings.AddAsync(displayName);
        if (!result.IsOk)
            return await FailAsync(result.Message);
        StatusMessage = $"Added {result.Value!.DisplayName}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRenameAsync(int id, string? displayName)
    {
        if (await UserIdAsync() is null)
            return Forbid();
        var result = await mountings.RenameAsync(id, displayName);
        if (!result.IsOk)
            return await FailAsync(result.Message);
        StatusMessage = "Renamed.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRetireAsync(int id)
    {
        if (await UserIdAsync() is not { } userId)
            return Forbid();
        var result = await mountings.RetireAsync(userId, id);
        if (!result.IsOk)
            return await FailAsync(result.Message);
        StatusMessage = $"{result.Value!.DisplayName} is retired.";
        return RedirectToPage();
    }

    protected override async Task LoadAsync()
    {
        Types = await mountings.ListAsync();
        Units = await mountings.UnitCountsAsync();
    }
}
