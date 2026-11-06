using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Manufacturing;
using Microsoft.AspNetCore.Mvc;

namespace Intelimensa.Accounts.Pages.Manufacturing;

/// <summary>Import the catalog from XML (validate only, or apply) and export all of it.</summary>
[RequestSizeLimit(CatalogXml.MaxBytes + 64 * 1024)]
public class CatalogImportModel(UnitProvisioning provisioning, CatalogXml xml, ApplicationDbContext db)
    : ProductionPageModel(provisioning, db)
{
    public ImportReport? Report { get; private set; }

    public bool Validated { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnGetExportAsync()
    {
        if (await UserIdAsync() is null)
            return Forbid();
        return File(await xml.ExportAsync(), "application/xml", $"catalog-{DateTime.UtcNow:yyyyMMdd-HHmm}.xml");
    }

    public async Task<IActionResult> OnPostAsync(IFormFile? file, bool validateOnly)
    {
        if (await UserIdAsync() is not { } userId)
            return Forbid();
        if (file is null || file.Length == 0)
            return await FailAsync("Choose an XML file.");
        if (file.Length > CatalogXml.MaxBytes)
            return await FailAsync($"The file is larger than {CatalogXml.MaxBytes / (1024 * 1024)} MiB.");

        await using var stream = file.OpenReadStream();
        Report = await xml.ImportAsync(userId, stream, validateOnly);
        Validated = validateOnly;
        return Page();
    }

    protected override Task LoadAsync() => Task.CompletedTask;
}
