using System.Security.Claims;
using System.Security.Cryptography;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Manufacturing;
using Intelimensa.Accounts.Models;
using Intelimensa.Accounts.Security;
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
    private const int MaxSerialAttempts = 5;
    private const int MaxNoteLength = 500;

    public static IEndpointRouteBuilder MapManufacturingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/manufacturing")
            .RequireAuthorization("Manufacturer");

        group.MapGet("/options", GetOptions);

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
            Enum.GetNames<RekeyReason>().ToList()));

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
        if (region.Length == 0)
            return Results.BadRequest(
                $"Region is required. Allowed: {string.Join(", ", options.Value.RegionCodes.Keys.Order())}.");
        if (!options.Value.RegionCodes.ContainsKey(region))
            return Results.BadRequest(
                $"Unknown region '{request.Region}'. Allowed: {string.Join(", ", options.Value.RegionCodes.Keys.Order())}.");

        if (ValidateFirmware(request.FirmwareVersion) is { } firmwareError)
            return firmwareError;
        var firmware = request.FirmwareVersion.Trim();

        var product = productCode.ToUpperInvariant();
        var prefix = product + region + SerialNumber.FormatVersion;
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
                .Where(d => d.SerialNumber.StartsWith(prefix))
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
            db.BciDeviceEvents.Add(new BciDeviceEvent
            {
                BciDevice = unit,
                Type = BciDeviceEventType.Reserved,
                OccurredAt = now,
                UserId = userId,
                FirmwareVersionAfter = firmware,
            });

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                foreach (var entry in db.ChangeTracker.Entries().ToList())
                    entry.State = EntityState.Detached;
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

        if (ValidateFirmware(request.FirmwareVersion) is { } firmwareError)
            return firmwareError;
        var firmware = request.FirmwareVersion.Trim();

        // Idempotent: a retried confirm for an already-manufactured unit just reports success.
        if (unit.Status == BciDeviceStatus.Reserved)
        {
            var now = DateTimeOffset.UtcNow;
            var before = unit.CurrentFirmwareVersion;

            // ManufacturedAt/ProducedAt record the *first* confirmation; a reworked unit keeps them.
            if (unit.ManufacturedAt is null)
            {
                unit.ManufacturedAt = now;
                unit.ProducedAt = DateOnly.FromDateTime(now.UtcDateTime);
            }

            unit.Status = BciDeviceStatus.Manufactured;
            ApplyFirmware(unit, firmware, now);
            AddEvent(db, unit, BciDeviceEventType.Confirmed, userId, now, before: before, after: firmware);

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
            // A reworked unit awaiting re-confirmation may still have participants paired to it;
            // voiding would strand them.
            if (await db.AccountDevices.AnyAsync(ad => ad.BciDeviceId == unit.Id))
                return Results.Conflict("This unit has participants paired to it and can't be voided; re-flash and confirm it instead.");

            unit.Status = BciDeviceStatus.Voided;
            unit.RegistrationCodeHash = null;
            AddEvent(db, unit, BciDeviceEventType.Voided, userId, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
            logger.LogInformation("Unit {Serial} voided by {UserId}", unit.SerialNumber, userId);
        }

        return Results.Ok(ToResponse(unit));
    }

    private static async Task<IResult> RekeyAsync(
        RekeyUnitRequest request,
        ClaimsPrincipal user,
        HttpResponse response,
        ApplicationDbContext db,
        ILogger<ManufacturingOptions> logger)
    {
        if (await RequireUsableAccountAsync(user, db) is not { } userId)
            return Results.Unauthorized();

        // Parsed by hand (not bound as an enum) so an unknown value gets a clear 400 rather than a
        // JSON binding failure, and numeric strings aren't accepted.
        var reasonName = Enum.GetNames<RekeyReason>()
            .FirstOrDefault(n => n.Equals(request.Reason?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (reasonName is null)
            return Results.BadRequest($"Reason is required: {string.Join(", ", Enum.GetNames<RekeyReason>())}.");
        var reason = Enum.Parse<RekeyReason>(reasonName);
        if (ValidateNote(request.Note) is { } noteError)
            return noteError;
        if (ValidateFirmware(request.FirmwareVersion) is { } firmwareError)
            return firmwareError;

        var unit = await FindAsync(db, request.SerialNumber);
        if (unit is null)
            return Results.NotFound("Unknown serial number.");
        if (unit.Status == BciDeviceStatus.Voided)
            return Results.Conflict("This unit's reservation was voided.");

        // The old code stops working immediately and the unit isn't registerable to new
        // participants until it's re-flashed and confirmed. Existing account pairings are
        // unaffected. The firmware version is recorded as *intended* only: it's applied to the unit
        // at confirm, so a failed rework can't leave the record claiming a write that never happened.
        var now = DateTimeOffset.UtcNow;
        var code = RegistrationCode.Generate();
        unit.RegistrationCodeHash = RegistrationCode.Hash(code);
        unit.Status = BciDeviceStatus.Reserved;
        unit.ReservedAt = now;
        unit.ReservedByUserId = userId;
        AddEvent(db, unit, BciDeviceEventType.Rekeyed, userId, now,
            reason: reason, note: request.Note?.Trim(),
            before: unit.CurrentFirmwareVersion, after: request.FirmwareVersion.Trim());
        await db.SaveChangesAsync();

        logger.LogInformation("Unit {Serial} re-keyed ({Reason}) by {UserId}", unit.SerialNumber, reason, userId);
        NoStore(response);
        return Results.Ok(Issued(unit, code));
    }

    private static async Task<IResult> FirmwareAsync(
        FirmwareWriteRequest request,
        ClaimsPrincipal user,
        ApplicationDbContext db,
        ILogger<ManufacturingOptions> logger)
    {
        if (await RequireUsableAccountAsync(user, db) is not { } userId)
            return Results.Unauthorized();

        if (ValidateNote(request.Note) is { } noteError)
            return noteError;
        if (ValidateFirmware(request.FirmwareVersion) is { } firmwareError)
            return firmwareError;

        var unit = await FindAsync(db, request.SerialNumber);
        if (unit is null)
            return Results.NotFound("Unknown serial number.");
        if (unit.Status != BciDeviceStatus.Manufactured)
            return Results.Conflict("Only a manufactured unit can record a firmware write; a reserved unit's firmware is recorded at confirm.");

        // A firmware write that leaves the registration code unchanged (the identity was read from
        // the unit before the erase and restored), so the label stays valid.
        var now = DateTimeOffset.UtcNow;
        var before = unit.CurrentFirmwareVersion;
        var firmware = request.FirmwareVersion.Trim();
        ApplyFirmware(unit, firmware, now);
        AddEvent(db, unit, BciDeviceEventType.FirmwareWritten, userId, now,
            note: request.Note?.Trim(), before: before, after: firmware);
        await db.SaveChangesAsync();

        logger.LogInformation("Unit {Serial} firmware written ({Before} -> {After}) by {UserId}",
            unit.SerialNumber, before, firmware, userId);
        return Results.Ok(ToResponse(unit));
    }

    /// <summary>
    /// The firmware-write rule: the timestamp always moves; the recorded version only changes when
    /// it actually differs.
    /// </summary>
    private static void ApplyFirmware(BciDevice unit, string version, DateTimeOffset now)
    {
        if (unit.CurrentFirmwareVersion != version)
            unit.CurrentFirmwareVersion = version;
        unit.LastFirmwareUpdatedAt = now;
    }

    private static void AddEvent(
        ApplicationDbContext db,
        BciDevice unit,
        BciDeviceEventType type,
        string userId,
        DateTimeOffset now,
        RekeyReason? reason = null,
        string? note = null,
        string? before = null,
        string? after = null) =>
        db.BciDeviceEvents.Add(new BciDeviceEvent
        {
            BciDeviceId = unit.Id,
            Type = type,
            OccurredAt = now,
            UserId = userId,
            Reason = reason,
            Note = string.IsNullOrWhiteSpace(note) ? null : note,
            FirmwareVersionBefore = before,
            FirmwareVersionAfter = after,
        });

    private static IResult? ValidateFirmware(string? firmwareVersion)
    {
        var firmware = firmwareVersion?.Trim() ?? string.Empty;
        return firmware.Length is 0 or > 50
            ? Results.BadRequest("FirmwareVersion is required (max 50 characters).")
            : null;
    }

    private static IResult? ValidateNote(string? note) =>
        note is { Length: > MaxNoteLength }
            ? Results.BadRequest($"Note is too long (max {MaxNoteLength} characters).")
            : null;

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

    private static Task<BciDevice?> FindAsync(ApplicationDbContext db, string? serialNumber) =>
        db.BciDevices.FindBySerialAsync(serialNumber);

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
        unit.ManufacturedAt,
        unit.LastFirmwareUpdatedAt);

    private static void NoStore(HttpResponse response) => response.Headers.CacheControl = "no-store";
}
