using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Manufacturing;
using Intelimensa.Accounts.Models;
using Intelimensa.Accounts.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Intelimensa.Accounts.Pages.Manufacturing;

/// <summary>
/// Shared base of the manual provisioning pages (reserve / serials / confirm / rekey / units): the
/// hand-driven version of the manufacturing station's calls, for flashing units without Quarry. They run
/// the same <see cref="UnitProvisioning"/> logic as <c>/api/manufacturing/units</c>. Gated by the
/// <c>Manufacturer</c> role (not Staff). A freshly issued registration code is rendered once, in the
/// response that created it; it's never kept in TempData or the database in plaintext.
/// </summary>
public abstract class ManufacturingPageModel(
    ApplicationDbContext db, UnitProvisioning provisioning, IOptions<ManufacturingOptions> options) : PageModel
{
    protected const int MaxBatch = 50;
    protected const int MaxSerialBatch = 200;

    protected ApplicationDbContext Db => db;

    protected UnitProvisioning Provisioning => provisioning;

    public List<string> DeviceTypes { get; private set; } = [];

    public List<string> Regions { get; private set; } = [];

    /// <summary>
    /// The published builds a programmer can write to a unit (MS-V1/MS-V2's firmware, MS-V3's factory
    /// image), newest first. Only these can be reserved against, confirmed or rekeyed to: the versions
    /// recorded on a unit come from the catalog, never from typing.
    /// </summary>
    public List<BuildOption> Builds { get; private set; } = [];

    /// <summary>Codes issued by this request. Shown once; reloading the page loses them.</summary>
    public List<IssuedRow> Issued { get; protected set; } = [];

    public string? IssuedHeading { get; protected set; }

    /// <summary>Serials held by this request (no codes: nothing secret here, unlike <see cref="Issued"/>).</summary>
    public List<string> Held { get; } = [];

    /// <summary>Serials from a hold request that couldn't be held, with the reason.</summary>
    public List<SerialFailure> Failures { get; protected set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync() => await LoadAsync();

    /// <summary>
    /// The chosen catalog build, if it's a published build a programmer can write to a unit of this
    /// device type. Otherwise an error message. The unit's recorded firmware (and, for MS-V3, bootloader)
    /// version are then the build's, so only production-ready, known images get recorded.
    /// </summary>
    protected async Task<(FirmwareBuild? Build, string? Error)> ResolveBuildAsync(string? deviceType, int buildId)
    {
        var type = deviceType?.Trim().ToLowerInvariant() ?? string.Empty;
        var build = await db.FirmwareBuilds.FirstOrDefaultAsync(b =>
            b.Id == buildId && b.Status == FirmwareBuildStatus.Published && b.DeviceType == type);
        return build is { FlashableByProgrammer: true }
            ? (build, null)
            : (null, $"Choose a published firmware build for {type.ToUpperInvariant()}.");
    }

    protected async Task<string?> DeviceTypeOfAsync(string? serialNumber) =>
        (await db.BciDevices.FindBySerialAsync(serialNumber))?.DeviceType;

    protected async Task<IActionResult> ShowCodesAsync()
    {
        Response.Headers.CacheControl = "no-store";
        await LoadAsync();
        return Page();
    }

    protected async Task<IActionResult> FailAsync(string? message)
    {
        ErrorMessage = message;
        await LoadAsync();
        return Page();
    }

    protected virtual async Task LoadAsync()
    {
        DeviceTypes = options.Value.ProductCodes.Keys.Select(k => k.ToLowerInvariant()).Order().ToList();
        Regions = options.Value.RegionCodes.Keys.Select(k => k.ToUpperInvariant()).Order().ToList();

        var published = (await db.FirmwareBuilds
                .Where(b => b.Status == FirmwareBuildStatus.Published).ToListAsync())
            .Where(b => b.FlashableByProgrammer)
            .OrderBy(b => b.DeviceType).ThenByDescending(b => b.PublishedAt) // client-side: SQLite can't order DateTimeOffset
            .ToList();
        Builds = published.Select(b => new BuildOption(
            b.Id, b.DeviceType,
            b.BootloaderVersion is null
                ? $"{b.DeviceType.ToUpperInvariant()} · {b.Version}"
                : $"{b.DeviceType.ToUpperInvariant()} · {b.Version} · bootloader {b.BootloaderVersion}")).ToList();
    }

    protected static IssuedRow ToRow(IssuedUnit issued) => new(
        SerialNumber.ToDisplay(issued.Unit.SerialNumber),
        RegistrationCode.ToLabelFormat(issued.Code),
        issued.Unit.DeviceType);

    public record BuildOption(int Id, string DeviceType, string Label);

    public record SerialFailure(string Serial, string Reason);

    public record IssuedRow(string SerialLabel, string CodeLabel, string DeviceType);
}

/// <summary>Model for <c>_BuildSelect</c>: one dropdown over the catalog builds.</summary>
public record BuildSelectModel(string Id, string Label, IReadOnlyList<ManufacturingPageModel.BuildOption> Builds);
