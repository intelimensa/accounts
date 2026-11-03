using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Manufacturing;
using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Intelimensa.Accounts.Pages.Manufacturing;

public class RekeyModel(ApplicationDbContext db, UnitProvisioning provisioning, IOptions<ManufacturingOptions> options)
    : ManufacturingPageModel(db, provisioning, options)
{
    public List<string> RekeyReasons { get; } = Enum.GetNames<RekeyReason>().Where(n => n != nameof(RekeyReason.Initial)).ToList();

    public async Task<IActionResult> OnPostAsync(string? serialNumber, string? reason, string? note, int firmwareBuildId)
    {
        if (await Provisioning.GetUsableUserIdAsync(User) is not { } userId)
            return Forbid();

        if (await DeviceTypeOfAsync(serialNumber) is not { } unitType)
            return await FailAsync("Unknown serial number.");
        var (build, buildError) = await ResolveBuildAsync(unitType, firmwareBuildId);
        if (build is null)
            return await FailAsync(buildError);

        var result = await Provisioning.RekeyAsync(userId, serialNumber, reason, note, build.Version, build.BootloaderVersion);
        if (!result.IsOk)
            return await FailAsync(result.Message);

        Issued.Add(ToRow(result.Value!));
        IssuedHeading = "New registration code issued; the old one no longer works";
        return await ShowCodesAsync();
    }
}
