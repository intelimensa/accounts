using System.Security.Claims;
using System.Security.Cryptography;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Manufacturing;
using Intelimensa.Accounts.Models;
using Intelimensa.Accounts.Security;
using Intelimensa.Accounts.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Intelimensa.Accounts.Api.Manufacturing;

/// <summary>
/// The provisioning API used by the manufacturing station app (MindStoneQuarry -- see
/// firmware/MindStoneQuarry-design.md). Restricted to the <c>Manufacturer</c> role. Lifecycle:
/// <c>reserve</c> issues a serial + registration code (unit is <see cref="BciDeviceStatus.Reserved"/>),
/// the station flashes and reads the unit back, then <c>confirm</c> submits the code it read back
/// and the unit becomes <see cref="BciDeviceStatus.Manufactured"/> -- the only state participants
/// can register. <c>void</c> abandons a reservation; <c>rekey</c> issues a fresh code for a unit
/// whose identity page must be rewritten (with a required reason); <c>firmware</c> records an
/// firmware write that leaves the code unchanged (identity preserved or restored). Every state change appends a
/// <see cref="BciDeviceEvent"/>. Codes are never logged or stored in events.
/// </summary>
public static class ManufacturingEndpoints
{
    public static IEndpointRouteBuilder MapManufacturingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/manufacturing")
            .RequireAuthorization("Manufacturer");

        group.MapGet("/options", GetOptions);

        var firmware = group.MapGroup("/firmware");
        firmware.MapGet("/", ListFirmwareAsync);
        firmware.MapGet("/{id:int}/file", DownloadFirmwareAsync);

        var units = group.MapGroup("/units");
        units.MapPost("/reserve", ReserveAsync);
        units.MapPost("/confirm", ConfirmAsync);
        units.MapPost("/void", VoidAsync);
        units.MapPost("/rekey", RekeyAsync);
        units.MapPost("/firmware", FirmwareAsync);

        return app;
    }

    private static IResult GetOptions(IOptions<ManufacturingOptions> options) =>
        Results.Ok(new ManufacturingOptionsResponse(
            options.Value.ProductCodes
                .OrderBy(p => p.Key)
                .Select(p => new ProductOption(p.Key.ToLowerInvariant(), p.Value.ToUpperInvariant()))
                .ToList(),
            options.Value.RegionCodes
                .OrderBy(r => r.Key)
                .Select(r => new RegionOption(r.Key.ToUpperInvariant(), r.Value))
                .ToList(),
            // Initial is the server's own choice for a held serial, never one a station picks.
            Enum.GetNames<RekeyReason>().Where(n => n != nameof(RekeyReason.Initial)).ToList()));

    /// <summary>
    /// The catalog the station picks from. Only Published builds; <c>deviceType</c> is optional and
    /// filters to one product (case-insensitive).
    /// </summary>
    private static async Task<IResult> ListFirmwareAsync(
        string? deviceType,
        ClaimsPrincipal user,
        ApplicationDbContext db,
        UnitProvisioning provisioning)
    {
        if (await provisioning.GetUsableUserIdAsync(user) is null)
            return Results.Unauthorized();

        var type = deviceType?.Trim().ToLowerInvariant();
        var query = db.FirmwareBuilds.Where(b => b.Status == FirmwareBuildStatus.Published);
        if (!string.IsNullOrEmpty(type))
            query = query.Where(b => b.DeviceType == type);

        var builds = await query.ToListAsync();
        // SQLite's EF provider can't ORDER BY DateTimeOffset -- sort client-side, newest first.
        return Results.Ok(builds
            .OrderBy(b => b.DeviceType).ThenBy(b => b.Kind).ThenByDescending(b => b.PublishedAt)
            .Select(b => new FirmwareBuildResponse(
                b.Id, b.DeviceType, b.Kind.ToString(), b.Version, b.BootloaderVersion, b.Notes,
                b.FileName, b.SizeBytes, b.Sha256, b.PublishedAt!.Value))
            .ToList());
    }

    private static async Task<IResult> DownloadFirmwareAsync(
        int id,
        ClaimsPrincipal user,
        HttpResponse response,
        ApplicationDbContext db,
        UnitProvisioning provisioning,
        IFirmwareStorage storage)
    {
        if (await provisioning.GetUsableUserIdAsync(user) is null)
            return Results.Unauthorized();

        var build = await db.FirmwareBuilds.FindAsync(id);
        if (build is null || build.Status != FirmwareBuildStatus.Published)
            return Results.NotFound("Unknown or unavailable firmware build.");

        // Immutable once uploaded, so the hash is a valid strong validator.
        response.Headers.ETag = $"\"{build.Sha256}\"";
        return Results.File(storage.OpenRead(build.StorageKey), "application/octet-stream", build.FileName);
    }

    private static async Task<IResult> ReserveAsync(
        ReserveUnitRequest request,
        ClaimsPrincipal user,
        HttpResponse response,
        UnitProvisioning provisioning)
    {
        if (await provisioning.GetUsableUserIdAsync(user) is not { } userId)
            return Results.Unauthorized();

        var result = await provisioning.ReserveAsync(
            userId, request.DeviceType, request.Region, request.FirmwareVersion, request.BootloaderVersion);
        if (!result.IsOk)
            return ToError(result);

        NoStore(response);
        var issued = result.Value!;
        return Results.Created($"/api/manufacturing/units/{issued.Unit.SerialNumber}", Issued(issued.Unit, issued.Code));
    }

    private static async Task<IResult> ConfirmAsync(
        ConfirmUnitRequest request,
        ClaimsPrincipal user,
        UnitProvisioning provisioning)
    {
        if (await provisioning.GetUsableUserIdAsync(user) is not { } userId)
            return Results.Unauthorized();

        var result = await provisioning.ConfirmAsync(
            userId, request.SerialNumber, request.RegistrationCode, request.FirmwareVersion, request.BootloaderVersion);
        return result.IsOk ? Results.Ok(ToResponse(result.Value!)) : ToError(result);
    }

    private static async Task<IResult> VoidAsync(
        UnitRequest request,
        ClaimsPrincipal user,
        UnitProvisioning provisioning)
    {
        if (await provisioning.GetUsableUserIdAsync(user) is not { } userId)
            return Results.Unauthorized();

        var result = await provisioning.VoidAsync(userId, request.SerialNumber);
        return result.IsOk ? Results.Ok(ToResponse(result.Value!)) : ToError(result);
    }

    private static async Task<IResult> RekeyAsync(
        RekeyUnitRequest request,
        ClaimsPrincipal user,
        HttpResponse response,
        UnitProvisioning provisioning)
    {
        if (await provisioning.GetUsableUserIdAsync(user) is not { } userId)
            return Results.Unauthorized();

        var result = await provisioning.RekeyAsync(
            userId, request.SerialNumber, request.Reason, request.Note, request.FirmwareVersion, request.BootloaderVersion);
        if (!result.IsOk)
            return ToError(result);

        NoStore(response);
        var issued = result.Value!;
        return Results.Ok(Issued(issued.Unit, issued.Code));
    }

    private static async Task<IResult> FirmwareAsync(
        FirmwareWriteRequest request,
        ClaimsPrincipal user,
        ApplicationDbContext db,
        UnitProvisioning provisioning,
        ILogger<ManufacturingOptions> logger)
    {
        if (await provisioning.GetUsableUserIdAsync(user) is not { } userId)
            return Results.Unauthorized();

        if (UnitProvisioning.ValidateNote(request.Note) is { } noteError)
            return Results.BadRequest(noteError);
        if (UnitProvisioning.ValidateFirmware(request.FirmwareVersion) is { } firmwareError)
            return Results.BadRequest(firmwareError);
        if (UnitProvisioning.ValidateBootloader(request.BootloaderVersion) is { } bootloaderError)
            return Results.BadRequest(bootloaderError);

        var unit = await db.BciDevices.FindBySerialAsync(request.SerialNumber);
        if (unit is null)
            return Results.NotFound("Unknown serial number.");
        if (unit.Status != BciDeviceStatus.Manufactured)
            return Results.Conflict("Only a manufactured unit can record a firmware write; a reserved unit's firmware is recorded at confirm.");

        // A firmware write that leaves the registration code unchanged (the identity was read from
        // the unit before the erase and restored), so the label stays valid.
        var now = DateTimeOffset.UtcNow;
        var before = unit.CurrentFirmwareVersion;
        var bootloaderBefore = unit.BootloaderVersion;
        var firmware = request.FirmwareVersion.Trim();
        UnitProvisioning.ApplyFirmware(unit, firmware, now);
        // An app update over USB doesn't touch the bootloader, so a client leaves this out. It's sent
        // after a programmer flash restored the same identity, which rewrites the bootloader too.
        UnitProvisioning.ApplyBootloader(unit, UnitProvisioning.NormalizeBootloader(request.BootloaderVersion));
        UnitProvisioning.AddEvent(db, unit, BciDeviceEventType.FirmwareWritten, userId, now,
            note: request.Note?.Trim(), before: before, after: firmware,
            bootloaderBefore: bootloaderBefore, bootloaderAfter: unit.BootloaderVersion);
        await db.SaveChangesAsync();

        logger.LogInformation("Unit {Serial} firmware written ({Before} -> {After}) by {UserId}",
            unit.SerialNumber, before, firmware, userId);
        return Results.Ok(ToResponse(unit));
    }

    private static IResult ToError<T>(ProvisionResult<T> result) => result.Status switch
    {
        ProvisionStatus.NotFound => Results.NotFound(result.Message),
        ProvisionStatus.Conflict => Results.Conflict(result.Message),
        ProvisionStatus.Unavailable => Results.Problem(result.Message, statusCode: 503),
        _ => Results.BadRequest(result.Message),
    };

    private static IssuedUnitResponse Issued(BciDevice unit, string code) => new(
        unit.SerialNumber,
        SerialNumber.ToDisplay(unit.SerialNumber),
        code,
        RegistrationCode.ToLabelFormat(code),
        unit.DeviceType,
        unit.ReservedAt!.Value);

    private static UnitResponse ToResponse(BciDevice unit) => new(
        unit.SerialNumber,
        SerialNumber.ToDisplay(unit.SerialNumber),
        unit.DeviceType,
        unit.Status,
        unit.CurrentFirmwareVersion,
        unit.BootloaderVersion,
        unit.ManufacturedAt,
        unit.LastFirmwareUpdatedAt);

    private static void NoStore(HttpResponse response) => response.Headers.CacheControl = "no-store";
}
