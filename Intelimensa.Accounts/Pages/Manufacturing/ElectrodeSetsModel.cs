using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Manufacturing;
using Microsoft.AspNetCore.Mvc;

namespace Intelimensa.Accounts.Pages.Manufacturing;

/// <summary>All electrode sets with how many units carry each, and a form for a new one.</summary>
public class ElectrodeSetsModel(UnitProvisioning provisioning, ElectrodeSetService sets, ApplicationDbContext db)
    : ProductionPageModel(provisioning, db)
{
    public List<ElectrodeSetService.SetRow> Rows { get; private set; } = [];

    public Dictionary<string, string> Emails { get; private set; } = [];

    public Task OnGetAsync() => LoadAsync();

    public async Task<IActionResult> OnPostCreateAsync(string? name, string? version, string? description)
    {
        if (await UserIdAsync() is not { } userId)
            return Forbid();
        var result = await sets.CreateAsync(userId, name, version, description);
        if (!result.IsOk)
            return await FailAsync(result.Message);
        return RedirectToPage("/Manufacturing/ElectrodeSet", new { id = result.Value!.Id });
    }

    protected override async Task LoadAsync()
    {
        Rows = await sets.ListAsync();
        Emails = await EmailsAsync(Rows.Select(r => r.Set.LastModifiedByUserId));
    }
}
