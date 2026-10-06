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
/// Hand-driven version of the manufacturing station's provisioning calls (reserve / confirm / void /
/// rekey), for flashing units without Quarry. It runs the same <see cref="UnitProvisioning"/> logic
/// as <c>/api/manufacturing/units</c>. Gated by the <c>Manufacturer</c> role (not Staff). A freshly
/// issued registration code is rendered once, in the response that created it; it's never kept in
/// TempData or the database in plaintext.
/// </summary>
public class IndexModel(
    ApplicationDbContext db, UnitProvisioning provisioning, IOptions<ManufacturingOptions> options) : PageModel
{
    private const int MaxBatch = 50;
    private const int MaxSerialBatch = 200;
    private const int MaxListed = 200;

    public List<string> DeviceTypes { get; private set; } = [];

    public List<string> Regions { get; private set; } = [];

    public List<string> RekeyReasons { get; } = Enum.GetNames<RekeyReason>().Where(n => n != nameof(RekeyReason.Initial)).ToList();

    public List<UnitRow> Units { get; private set; } = [];

    /// <summary>
    /// The published builds a programmer can write to a unit (MS-V1/MS-V2's firmware, MS-V3's factory
    /// image), newest first. Only these can be reserved against, confirmed or rekeyed to: the versions
    /// recorded on a unit come from the catalog, never from typing.
    /// </summary>
    public List<BuildOption> Builds { get; private set; } = [];

    /// <summary>Codes issued by this request. Shown once; reloading the page loses them.</summary>
    public List<IssuedRow> Issued { get; private set; } = [];

    public string? IssuedHeading { get; private set; }

    /// <summary>Serials held by this request (no codes: nothing secret here, unlike <see cref="Issued"/>).</summary>
    public List<string> Held { get; } = [];

    /// <summary>Serials from a hold request that couldn't be held, with the reason.</summary>
    public List<SerialFailure> Failures { get; private set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostReserveAsync(
        string? deviceType, string? region, int firmwareBuildId, int count)
    {
        if (await provisioning.GetUsableUserIdAsync(User) is not { } userId)
            return Forbid();
        if (count is < 1 or > MaxBatch)
            return await FailAsync($"Count must be between 1 and {MaxBatch}.");
        var (build, buildError) = await ResolveBuildAsync(deviceType, firmwareBuildId);
        if (build is null)
            return await FailAsync(buildError);

        for (var i = 0; i < count; i++)
        {
            var result = await provisioning.ReserveAsync(userId, deviceType, region, build.Version, build.BootloaderVersion);
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

    /// <summary>
    /// Holds serials that already exist outside the server (e.g. printed on casings): only the serial is
    /// reserved, with no code and no firmware, so it can't be issued to another unit. The first code
    /// comes from a rekey when the unit is flashed.
    /// </summary>
    public async Task<IActionResult> OnPostHoldSerialsAsync(string? serials)
    {
        if (await provisioning.GetUsableUserIdAsync(User) is not { } userId)
            return Forbid();

        // One serial per line, but commas/spaces are tolerated too. Serials never contain whitespace.
        // De-duplicated on the normalized form so a repeated line isn't reported as "already issued".
        var tokens = (serials ?? string.Empty)
            .Split([' ', '\t', '\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => SerialNumber.TryNormalize(t, out var canonical) ? canonical : t)
            .Distinct()
            .ToList();
        if (tokens.Count == 0)
            return await FailAsync("Enter at least one serial.");
        if (tokens.Count > MaxSerialBatch)
            return await FailAsync($"At most {MaxSerialBatch} serials at a time.");

        // Each serial stands alone: one bad serial doesn't stop the others.
        foreach (var token in tokens)
        {
            var result = await provisioning.HoldSerialAsync(userId, token);
            if (result.IsOk)
                Held.Add(SerialNumber.ToDisplay(result.Value!.SerialNumber));
            else
                Failures.Add(new SerialFailure(SerialNumber.ToDisplay(token), result.Message ?? "Failed."));
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostConfirmAsync(
        string? serialNumber, string? registrationCode, int firmwareBuildId)
    {
        if (await provisioning.GetUsableUserIdAsync(User) is not { } userId)
            return Forbid();

        if (await DeviceTypeOfAsync(serialNumber) is not { } unitType)
            return await FailAsync("Unknown serial number.");
        var (build, buildError) = await ResolveBuildAsync(unitType, firmwareBuildId);
        if (build is null)
            return await FailAsync(buildError);

        var result = await provisioning.ConfirmAsync(userId, serialNumber, registrationCode, build.Version, build.BootloaderVersion);
        if (!result.IsOk)
            return await FailAsync(result.Message);

        StatusMessage = $"{SerialNumber.ToDisplay(result.Value!.SerialNumber)} is confirmed (Manufactured).";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostVoidAsync(string? serialNumber)
    {
        if (await provisioning.GetUsableUserIdAsync(User) is not { } userId)
            return Forbid();

        var result = await provisioning.VoidAsync(userId, serialNumber);
        if (!result.IsOk)
            return await FailAsync(result.Message);

        StatusMessage = $"{SerialNumber.ToDisplay(result.Value!.SerialNumber)} is voided.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRekeyAsync(
        string? serialNumber, string? reason, string? note, int firmwareBuildId)
    {
        if (await provisioning.GetUsableUserIdAsync(User) is not { } userId)
            return Forbid();

        if (await DeviceTypeOfAsync(serialNumber) is not { } unitType)
            return await FailAsync("Unknown serial number.");
        var (build, buildError) = await ResolveBuildAsync(unitType, firmwareBuildId);
        if (build is null)
            return await FailAsync(buildError);

        var result = await provisioning.RekeyAsync(userId, serialNumber, reason, note, build.Version, build.BootloaderVersion);
        if (!result.IsOk)
            return await FailAsync(result.Message);

        Issued.Add(ToRow(result.Value!));
        IssuedHeading = "New registration code issued; the old one no longer works";
        return await ShowCodesAsync();
    }

    /// <summary>
    /// The chosen catalog build, if it's a published build a programmer can write to a unit of this
    /// device type. Otherwise an error message. The unit's recorded firmware (and, for MS-V3, bootloader)
    /// version are then the build's, so only production-ready, known images get recorded.
    /// </summary>
    private async Task<(FirmwareBuild? Build, string? Error)> ResolveBuildAsync(string? deviceType, int buildId)
    {
        var type = deviceType?.Trim().ToLowerInvariant() ?? string.Empty;
        var build = await db.FirmwareBuilds.FirstOrDefaultAsync(b =>
            b.Id == buildId && b.Status == FirmwareBuildStatus.Published && b.DeviceType == type);
        return build is { FlashableByProgrammer: true }
            ? (build, null)
            : (null, $"Choose a published firmware build for {type.ToUpperInvariant()}.");
    }

    private async Task<string?> DeviceTypeOfAsync(string? serialNumber) =>
        (await db.BciDevices.FindBySerialAsync(serialNumber))?.DeviceType;

    private async Task<IActionResult> ShowCodesAsync()
    {
        Response.Headers.CacheControl = "no-store";
        await LoadAsync();
        return Page();
    }

    private async Task<IActionResult> FailAsync(string? message)
    {
        ErrorMessage = message;
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
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

        // Only units issued through this flow (units added via the staff form have no reservation).
        var units = await db.BciDevices.Where(d => d.ReservedAt != null).ToListAsync();
        // SQLite's EF provider can't ORDER BY DateTimeOffset -- sort client-side.
        Units = units
            .OrderByDescending(d => d.ReservedAt)
            .Take(MaxListed)
            .Select(d => new UnitRow(
                SerialNumber.ToDisplay(d.SerialNumber), d.DeviceType, d.Status,
                d.CurrentFirmwareVersion, d.BootloaderVersion, d.ReservedAt!.Value, d.IsHeld))
            .ToList();
    }

    private static IssuedRow ToRow(IssuedUnit issued) => new(
        SerialNumber.ToDisplay(issued.Unit.SerialNumber),
        RegistrationCode.ToLabelFormat(issued.Code),
        issued.Unit.DeviceType);

    public record UnitRow(
        string SerialLabel, string DeviceType, BciDeviceStatus Status,
        string? FirmwareVersion, string? BootloaderVersion, DateTimeOffset ReservedAt, bool Held);

    public record BuildOption(int Id, string DeviceType, string Label);

    public record SerialFailure(string Serial, string Reason);

    public record IssuedRow(string SerialLabel, string CodeLabel, string DeviceType);
}

/// <summary>Model for <c>_BuildSelect</c>: one dropdown over the catalog builds.</summary>
public record BuildSelectModel(string Id, string Label, IReadOnlyList<IndexModel.BuildOption> Builds);
