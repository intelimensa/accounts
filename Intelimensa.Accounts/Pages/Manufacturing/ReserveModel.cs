using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Manufacturing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Intelimensa.Accounts.Pages.Manufacturing;

public class ReserveModel(ApplicationDbContext db, UnitProvisioning provisioning, IOptions<ManufacturingOptions> options)
    : ManufacturingPageModel(db, provisioning, options)
{
    public async Task<IActionResult> OnPostAsync(string? deviceType, string? region, int firmwareBuildId, int count)
    {
        if (await Provisioning.GetUsableUserIdAsync(User) is not { } userId)
            return Forbid();
        if (count is < 1 or > MaxBatch)
            return await FailAsync($"Count must be between 1 and {MaxBatch}.");
        var (build, buildError) = await ResolveBuildAsync(deviceType, firmwareBuildId);
        if (build is null)
            return await FailAsync(buildError);

        for (var i = 0; i < count; i++)
        {
            var result = await Provisioning.ReserveAsync(userId, deviceType, region, build.Version, build.BootloaderVersion);
            if (!result.IsOk)
            {
                // Units already reserved in this batch stay reserved, so show their codes: they're
                // not recoverable later.
                ErrorMessage = Issued.Count > 0
                    ? $"{result.Message} Reserved {Issued.Count} of {count} before this failed."
                    : result.Message;
                break;
            }
            Issued.Add(ToRow(result.Value!));
        }

        IssuedHeading = $"Reserved {Issued.Count} unit(s)";
        return await ShowCodesAsync();
    }
}
