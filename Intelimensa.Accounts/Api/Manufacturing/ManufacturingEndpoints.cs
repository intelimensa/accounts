using System.Security.Claims;
using System.Security.Cryptography;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Manufacturing;
using Intelimensa.Accounts.Models;
using Intelimensa.Accounts.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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
/// that needs re-flashing (lost/leaked label). Codes are never logged.
/// </summary>
public static class ManufacturingEndpoints
{
    private const int MaxSerialAttempts = 5;

    public static IEndpointRouteBuilder MapManufacturingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/manufacturing/units")
            .RequireAuthorization("Manufacturer");

        group.MapPost("/reserve", ReserveAsync);
        group.MapPost("/confirm", ConfirmAsync);
        group.MapPost("/void", VoidAsync);
        group.MapPost("/rekey", RekeyAsync);

        return app;
    }

    private static async Task<IResult> ReserveAsync(
        ReserveUnitRequest request,
        ClaimsPrincipal user,
        HttpResponse response,
        ApplicationDbContext db,
        IOptions<ManufacturingOptions> options,
        ILogger<ManufacturingOptions> logger)
    {
        if (await RequireUsableAccountAsync(user, db) is not { } userId)
            return Results.Unauthorized();

        var deviceType = request.DeviceType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!options.Value.ProductCodes.TryGetValue(deviceType, out var productCode))
            return Results.BadRequest($"Unknown device type '{request.DeviceType}'.");

        var region = request.Region?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!options.Value.RegionCodes.ContainsKey(region))
            return Results.BadRequest(
                $"Unknown region '{request.Region}'. Allowed: {string.Join(", ", options.Value.RegionCodes.Keys.Order())}.");

        var firmware = request.FirmwareVersion?.Trim() ?? string.Empty;
        if (firmware.Length is 0 or > 50)
            return Results.BadRequest("FirmwareVersion is required (max 50 characters).");

        var product = productCode.ToUpperInvariant();
        var prefix = product + region + SerialNumber.FormatVersion;
        var serialPrefix = $"{product}-{region}{SerialNumber.FormatVersion}"; // as stored: the first dash follows PPPP
        var code = RegistrationCode.Generate();
        var now = DateTimeOffset.UtcNow;

        // Next sequence for this product+region = highest issued (voided serials included, so a
        // serial is never reused) plus a random small step, or the configured start for the first
        // unit. The random step keeps serials from revealing exactly how many units exist. The
        // unique index on SerialNumber makes a concurrent reserve collide instead of duplicate;
        // retry picks up the new maximum.
        for (var attempt = 0; attempt < MaxSerialAttempts; attempt++)
        {
            var existing = await db.BciDevices
                .Where(d => d.SerialNumber.StartsWith(serialPrefix))
                .Select(d => d.SerialNumber)
                .ToListAsync();

            var highest = existing
                .Select(ser => SerialNumber.TryParse(ser, out var p, out var seq) && p == prefix ? seq : -1)
                .DefaultIfEmpty(-1)
                .Max();
            var next = highest < 0
                ? options.Value.SequenceStart
                : highest + RandomNumberGenerator.GetInt32(1, options.Value.MaxSequenceStep + 1);
            if (next > SerialNumber.MaxSequence)
                return Results.Problem("Serial number space exhausted for this product/region.", statusCode: 409);

            var serial = SerialNumber.Format(product, region[0], next);
            var unit = new BciDevice
            {
                SerialNumber = serial,
                DeviceType = deviceType,
                ProducedAt = DateOnly.FromDateTime(now.UtcDateTime),
                CurrentFirmwareVersion = firmware,
                Status = BciDeviceStatus.Reserved,
                RegistrationCodeHash = RegistrationCode.Hash(code),
                ReservedAt = now,
                ReservedByUserId = userId,
            };
            db.BciDevices.Add(unit);

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                db.Entry(unit).State = EntityState.Detached;
                continue;
            }

            logger.LogInformation("Unit {Serial} reserved by {UserId}", serial, userId);
            NoStore(response);
            return Results.Created($"/api/manufacturing/units/{serial}", Issued(unit, code));
        }

        return Results.Problem("Could not allocate a serial number; try again.", statusCode: 503);
    }

    private static async Task<IResult> ConfirmAsync(
        ConfirmUnitRequest request,
        ClaimsPrincipal user,
        ApplicationDbContext db,
        ILogger<ManufacturingOptions> logger)
    {
        if (await RequireUsableAccountAsync(user, db) is not { } userId)
            return Results.Unauthorized();

        var unit = await FindAsync(db, request.SerialNumber);
        if (unit is null)
            return Results.NotFound("Unknown serial number.");
        if (unit.Status == BciDeviceStatus.Voided)
            return Results.Conflict("This unit's reservation was voided.");

        // The station submits the code it read back from the unit, so a match proves the flashed
        // unit really carries the code the server issued.
        if (!RegistrationCode.Verify(request.RegistrationCode, unit.RegistrationCodeHash))
            return Results.BadRequest("Registration code does not match this unit.");

        var firmware = request.FirmwareVersion?.Trim() ?? string.Empty;
        if (firmware.Length is 0 or > 50)
            return Results.BadRequest("FirmwareVersion is required (max 50 characters).");

        // Idempotent: a retried confirm for an already-manufactured unit just reports success.
        if (unit.Status == BciDeviceStatus.Reserved)
        {
            unit.Status = BciDeviceStatus.Manufactured;
            unit.ManufacturedAt = DateTimeOffset.UtcNow;
            unit.CurrentFirmwareVersion = firmware;
            unit.ProducedAt = DateOnly.FromDateTime(unit.ManufacturedAt.Value.UtcDateTime);
            await db.SaveChangesAsync();
            logger.LogInformation("Unit {Serial} confirmed manufactured by {UserId}", unit.SerialNumber, userId);
        }

        return Results.Ok(ToResponse(unit));
    }

    private static async Task<IResult> VoidAsync(
        UnitRequest request,
        ClaimsPrincipal user,
        ApplicationDbContext db,
        ILogger<ManufacturingOptions> logger)
    {
        if (await RequireUsableAccountAsync(user, db) is not { } userId)
            return Results.Unauthorized();

        var unit = await FindAsync(db, request.SerialNumber);
        if (unit is null)
            return Results.NotFound("Unknown serial number.");
        if (unit.Status == BciDeviceStatus.Manufactured)
            return Results.Conflict("A manufactured unit can't be voided; use rekey to re-flash it.");

        if (unit.Status == BciDeviceStatus.Reserved)
        {
            unit.Status = BciDeviceStatus.Voided;
            unit.RegistrationCodeHash = null;
            await db.SaveChangesAsync();
            logger.LogInformation("Unit {Serial} voided by {UserId}", unit.SerialNumber, userId);
        }

        return Results.Ok(ToResponse(unit));
    }

    private static async Task<IResult> RekeyAsync(
        UnitRequest request,
        ClaimsPrincipal user,
        HttpResponse response,
        ApplicationDbContext db,
        ILogger<ManufacturingOptions> logger)
    {
        if (await RequireUsableAccountAsync(user, db) is not { } userId)
            return Results.Unauthorized();

        var unit = await FindAsync(db, request.SerialNumber);
        if (unit is null)
            return Results.NotFound("Unknown serial number.");
        if (unit.Status == BciDeviceStatus.Voided)
            return Results.Conflict("This unit's reservation was voided.");

        // The old code stops working immediately and the unit isn't registerable again until it's
        // re-flashed and confirmed. Existing account pairings are unaffected.
        var code = RegistrationCode.Generate();
        unit.RegistrationCodeHash = RegistrationCode.Hash(code);
        unit.Status = BciDeviceStatus.Reserved;
        unit.ReservedAt = DateTimeOffset.UtcNow;
        unit.ReservedByUserId = userId;
        unit.ManufacturedAt = null;
        await db.SaveChangesAsync();

        logger.LogInformation("Unit {Serial} re-keyed by {UserId}", unit.SerialNumber, userId);
        NoStore(response);
        return Results.Ok(Issued(unit, code));
    }

    /// <summary>
    /// Returns the caller's user id if their account is still usable. Re-checked on every call
    /// (not just at login) so revoking a station's account takes effect immediately rather than
    /// when its access token expires.
    /// </summary>
    private static async Task<string?> RequireUsableAccountAsync(ClaimsPrincipal user, ApplicationDbContext db)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
            return null;

        var account = await db.Accounts.FirstOrDefaultAsync(a => a.UserId == userId);
        return AccountPolicy.IsUsable(account) ? userId : null;
    }

    private static Task<BciDevice?> FindAsync(ApplicationDbContext db, string? serialNumber)
    {
        var serial = serialNumber?.Trim() ?? string.Empty;
        return db.BciDevices.FirstOrDefaultAsync(d => d.SerialNumber == serial);
    }

    private static IssuedUnitResponse Issued(BciDevice unit, string code) => new(
        unit.SerialNumber,
        code,
        RegistrationCode.ToLabelFormat(code),
        unit.DeviceType,
        unit.ReservedAt!.Value);

    private static UnitResponse ToResponse(BciDevice unit) => new(
        unit.SerialNumber, unit.DeviceType, unit.Status, unit.CurrentFirmwareVersion, unit.ManufacturedAt);

    private static void NoStore(HttpResponse response) => response.Headers.CacheControl = "no-store";
}
