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

    /// <summary>Include units that weren't issued here (added by staff, or before this flow existed): their checklists can be filled in too.</summary>
    [BindProperty(SupportsGet = true)]
    public bool All { get; set; }

    /// <summary>Show only units that have this step done / not done.</summary>
    [BindProperty(SupportsGet = true)]
    public ProductionStepType? Done { get; set; }

    [BindProperty(SupportsGet = true)]
    public ProductionStepType? NotDone { get; set; }

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

        // By default only units issued through this flow (units added via the staff form have no reservation).
        var units = await Db.BciDevices.Where(d => All || d.ReservedAt != null).ToListAsync();
        var steps = (await Db.ProductionSteps.Select(s => new { s.BciDeviceId, s.Step }).ToListAsync())
            .GroupBy(s => s.BciDeviceId).ToDictionary(g => g.Key, g => g.Select(s => s.Step).ToHashSet());

        // SQLite's EF provider can't ORDER BY DateTimeOffset -- sort client-side.
        Units = units
            .Select(d => new UnitRow(
                d.SerialNumber, SerialNumber.ToDisplay(d.SerialNumber), d.DeviceType, d.Status,
                d.CurrentFirmwareVersion, d.BootloaderVersion, d.ReservedAt ?? d.CreatedAt, d.IsHeld,
                steps.GetValueOrDefault(d.Id) ?? []))
            .Where(u => Done is null || u.DoneSteps.Contains(Done.Value))
            .Where(u => NotDone is null || !u.DoneSteps.Contains(NotDone.Value))
            .OrderByDescending(u => u.ReservedAt)
            .Take(MaxListed)
            .ToList();
    }

    public record UnitRow(
        string Serial, string SerialLabel, string DeviceType, BciDeviceStatus Status,
        string? FirmwareVersion, string? BootloaderVersion, DateTimeOffset ReservedAt, bool Held,
        HashSet<ProductionStepType> DoneSteps);
}
