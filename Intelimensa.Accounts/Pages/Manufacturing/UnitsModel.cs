using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Manufacturing;
using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Intelimensa.Accounts.Pages.Manufacturing;

public class UnitsModel(ApplicationDbContext db, UnitProvisioning provisioning, IOptions<ManufacturingOptions> options)
    : ManufacturingPageModel(db, provisioning, options)
{
    private const int MaxListed = 200;

    public List<UnitRow> Units { get; private set; } = [];

    public async Task<IActionResult> OnPostVoidAsync(string? serialNumber)
    {
        if (await Provisioning.GetUsableUserIdAsync(User) is not { } userId)
            return Forbid();

        var result = await Provisioning.VoidAsync(userId, serialNumber);
        if (!result.IsOk)
            return await FailAsync(result.Message);

        StatusMessage = $"{SerialNumber.ToDisplay(result.Value!.SerialNumber)} is voided.";
        return RedirectToPage();
    }

    protected override async Task LoadAsync()
    {
        await base.LoadAsync();

        // Only units issued through this flow (units added via the staff form have no reservation).
        var units = await Db.BciDevices.Where(d => d.ReservedAt != null).ToListAsync();
        // SQLite's EF provider can't ORDER BY DateTimeOffset -- sort client-side.
        Units = units
            .OrderByDescending(d => d.ReservedAt)
            .Take(MaxListed)
            .Select(d => new UnitRow(
                SerialNumber.ToDisplay(d.SerialNumber), d.DeviceType, d.Status,
                d.CurrentFirmwareVersion, d.BootloaderVersion, d.ReservedAt!.Value, d.IsHeld))
            .ToList();
    }

    public record UnitRow(
        string SerialLabel, string DeviceType, BciDeviceStatus Status,
        string? FirmwareVersion, string? BootloaderVersion, DateTimeOffset ReservedAt, bool Held);
}
