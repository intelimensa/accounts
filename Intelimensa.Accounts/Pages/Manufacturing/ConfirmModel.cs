using Intelimensa.Accounts.Models;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Manufacturing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Intelimensa.Accounts.Pages.Manufacturing;

public class ConfirmModel(ApplicationDbContext db, UnitProvisioning provisioning, IOptions<ManufacturingOptions> options)
    : ManufacturingPageModel(db, provisioning, options)
{
    public async Task<IActionResult> OnPostAsync(string? serialNumber, string? registrationCode, int firmwareBuildId)
    {
        if (await Provisioning.GetUsableUserIdAsync(User) is not { } userId)
            return Forbid();

        if (await DeviceTypeOfAsync(serialNumber) is not { } unitType)
            return await FailAsync("Unknown serial number.");
        var (build, buildError) = await ResolveBuildAsync(unitType, firmwareBuildId);
        if (build is null)
            return await FailAsync(buildError);

        var result = await Provisioning.ConfirmAsync(userId, serialNumber, registrationCode, build.Version, build.BootloaderVersion,
            ProductionStepSource.Manual);
        if (!result.IsOk)
            return await FailAsync(result.Message);

        StatusMessage = $"{SerialNumber.ToDisplay(result.Value!.SerialNumber)} is confirmed (Manufactured).";
        return RedirectToPage();
    }
}
