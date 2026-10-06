using System.Security.Claims;
using System.Security.Cryptography;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Intelimensa.Accounts.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Intelimensa.Accounts.Manufacturing;

public enum ProvisionStatus
{
    Ok,
    BadRequest,
    NotFound,
    Conflict,
    Unavailable,
}

public record ProvisionResult<T>(ProvisionStatus Status, T? Value, string? Message = null)
{
    public bool IsOk => Status == ProvisionStatus.Ok;
}

/// <summary>A unit together with the plaintext registration code that was just issued -- the only time it exists.</summary>
public record IssuedUnit(BciDevice Unit, string Code);

/// <summary>
/// The reserve / confirm / void / rekey lifecycle (see <c>ManufacturingEndpoints</c> for the flow),
/// shared by the manufacturing API and the <c>/Manufacturing</c> web pages so both run the same
/// allocation, validation, event history and logging. Callers authorize first
/// (<see cref="GetUsableUserIdAsync"/>) and pass the user id in. Codes are never logged or stored
/// in events.
/// </summary>
public class UnitProvisioning(
    ApplicationDbContext db,
    IOptions<ManufacturingOptions> options,
    ILogger<UnitProvisioning> logger)
{
    private const int MaxSerialAttempts = 5;
    private const int MaxNoteLength = 500;

    /// <summary>
    /// Returns the caller's user id if their account is still usable. Re-checked on every call
    /// (not just at login) so revoking a station's account takes effect immediately rather than
    /// when its access token or cookie expires.
    /// </summary>
    public async Task<string?> GetUsableUserIdAsync(ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
            return null;

        var account = await db.Accounts.FirstOrDefaultAsync(a => a.UserId == userId);
        return AccountPolicy.IsUsable(account) ? userId : null;
    }

    public async Task<ProvisionResult<IssuedUnit>> ReserveAsync(
        string userId, string? deviceTypeInput, string? regionInput, string? firmwareVersion, string? bootloaderVersion)
    {
        var deviceType = deviceTypeInput?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!options.Value.ProductCodes.TryGetValue(deviceType, out var productCode))
            return Fail<IssuedUnit>(ProvisionStatus.BadRequest, $"Unknown device type '{deviceTypeInput}'.");

        var region = regionInput?.Trim().ToUpperInvariant() ?? string.Empty;
        if (region.Length == 0)
            return Fail<IssuedUnit>(ProvisionStatus.BadRequest,
                $"Region is required. Allowed: {string.Join(", ", options.Value.RegionCodes.Keys.Order())}.");
        if (!options.Value.RegionCodes.ContainsKey(region))
            return Fail<IssuedUnit>(ProvisionStatus.BadRequest,
                $"Unknown region '{regionInput}'. Allowed: {string.Join(", ", options.Value.RegionCodes.Keys.Order())}.");

        if (ValidateFirmware(firmwareVersion) is { } firmwareError)
            return Fail<IssuedUnit>(ProvisionStatus.BadRequest, firmwareError);
        var firmware = firmwareVersion!.Trim();
        if (ValidateBootloader(bootloaderVersion) is { } bootloaderError)
            return Fail<IssuedUnit>(ProvisionStatus.BadRequest, bootloaderError);
        var bootloader = NormalizeBootloader(bootloaderVersion);

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
                return Fail<IssuedUnit>(ProvisionStatus.Conflict, "Serial number space exhausted for this product/region.");

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
                BootloaderVersionAfter = bootloader, // intended; applied to the unit at confirm
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
            return new(ProvisionStatus.Ok, new IssuedUnit(unit, code));
        }

        return Fail<IssuedUnit>(ProvisionStatus.Unavailable, "Could not allocate a serial number; try again.");
    }

    /// <summary>
    /// Reserves a serial the caller already has (e.g. one printed on a casing before the unit is
    /// ready to flash) instead of allocating the next one. The serial must be well-formed (its
    /// check character catches typos), name a configured product and region, and never have been
    /// issued -- voided serials included, as with allocation. The device type comes from the
    /// serial's product code. Everything after that is the normal lifecycle: it's Reserved, gets a
    /// fresh registration code (returned once), and is confirmed after flashing. The allocator
    /// continues from the highest serial per product+region, so a high hand-picked serial moves
    /// later automatic ones above it, never into a collision.
    /// </summary>
    public async Task<ProvisionResult<IssuedUnit>> ReserveSerialAsync(
        string userId, string? serialInput, string? firmwareVersion, string? bootloaderVersion)
    {
        if (!SerialNumber.TryNormalize(serialInput, out var serial))
            return Fail<IssuedUnit>(ProvisionStatus.BadRequest,
                "Not a valid serial (check the characters; the last one is a check character).");

        var product = serial[..4];
        if (DeviceTypeForSerial(serial) is not { } deviceType)
            return Fail<IssuedUnit>(ProvisionStatus.BadRequest, $"Unknown product code '{product}'.");

        var region = serial[4].ToString();
        if (!options.Value.RegionCodes.ContainsKey(region))
            return Fail<IssuedUnit>(ProvisionStatus.BadRequest,
                $"Unknown region '{region}'. Allowed: {string.Join(", ", options.Value.RegionCodes.Keys.Order())}.");

        if (ValidateFirmware(firmwareVersion) is { } firmwareError)
            return Fail<IssuedUnit>(ProvisionStatus.BadRequest, firmwareError);
        var firmware = firmwareVersion!.Trim();
        if (ValidateBootloader(bootloaderVersion) is { } bootloaderError)
            return Fail<IssuedUnit>(ProvisionStatus.BadRequest, bootloaderError);
        var bootloader = NormalizeBootloader(bootloaderVersion);

        if (await db.BciDevices.AnyAsync(d => d.SerialNumber == serial))
            return Fail<IssuedUnit>(ProvisionStatus.Conflict, "This serial has already been issued.");

        var code = RegistrationCode.Generate();
        var now = DateTimeOffset.UtcNow;
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
            BootloaderVersionAfter = bootloader, // intended; applied to the unit at confirm
        });

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Lost a race for the same serial (unique index); detach so the context stays usable.
            foreach (var entry in db.ChangeTracker.Entries().ToList())
                entry.State = EntityState.Detached;
            return Fail<IssuedUnit>(ProvisionStatus.Conflict, "This serial has already been issued.");
        }

        logger.LogInformation("Unit {Serial} reserved by serial by {UserId}", serial, userId);
        return new(ProvisionStatus.Ok, new IssuedUnit(unit, code));
    }

    /// <summary>The configured device type whose product code a canonical serial starts with, or null.</summary>
    public string? DeviceTypeForSerial(string canonicalSerial)
    {
        var product = canonicalSerial.Length >= 4 ? canonicalSerial[..4] : canonicalSerial;
        return options.Value.ProductCodes
            .Where(p => p.Value.Equals(product, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Key.ToLowerInvariant())
            .FirstOrDefault();
    }

    public async Task<ProvisionResult<BciDevice>> ConfirmAsync(
        string userId, string? serialNumber, string? registrationCode, string? firmwareVersion, string? bootloaderVersion)
    {
        var unit = await db.BciDevices.FindBySerialAsync(serialNumber);
        if (unit is null)
            return Fail<BciDevice>(ProvisionStatus.NotFound, "Unknown serial number.");
        if (unit.Status == BciDeviceStatus.Voided)
            return Fail<BciDevice>(ProvisionStatus.Conflict, "This unit's reservation was voided.");

        // The station submits the code it read back from the unit, so a match proves the flashed
        // unit really carries the code the server issued.
        if (!RegistrationCode.Verify(registrationCode, unit.RegistrationCodeHash))
            return Fail<BciDevice>(ProvisionStatus.BadRequest, "Registration code does not match this unit.");

        if (ValidateFirmware(firmwareVersion) is { } firmwareError)
            return Fail<BciDevice>(ProvisionStatus.BadRequest, firmwareError);
        var firmware = firmwareVersion!.Trim();
        if (ValidateBootloader(bootloaderVersion) is { } bootloaderError)
            return Fail<BciDevice>(ProvisionStatus.BadRequest, bootloaderError);

        // Idempotent: a retried confirm for an already-manufactured unit just reports success.
        if (unit.Status == BciDeviceStatus.Reserved)
        {
            var now = DateTimeOffset.UtcNow;
            var before = unit.CurrentFirmwareVersion;
            var bootloaderBefore = unit.BootloaderVersion;

            // ManufacturedAt/ProducedAt record the *first* confirmation; a reworked unit keeps them.
            if (unit.ManufacturedAt is null)
            {
                unit.ManufacturedAt = now;
                unit.ProducedAt = DateOnly.FromDateTime(now.UtcDateTime);
            }

            unit.Status = BciDeviceStatus.Manufactured;
            ApplyFirmware(unit, firmware, now);
            ApplyBootloader(unit, NormalizeBootloader(bootloaderVersion));
            AddEvent(db, unit, BciDeviceEventType.Confirmed, userId, now, before: before, after: firmware,
                bootloaderBefore: bootloaderBefore, bootloaderAfter: unit.BootloaderVersion);

            await db.SaveChangesAsync();
            logger.LogInformation("Unit {Serial} confirmed manufactured by {UserId}", unit.SerialNumber, userId);
        }

        return new(ProvisionStatus.Ok, unit);
    }

    public async Task<ProvisionResult<BciDevice>> VoidAsync(string userId, string? serialNumber)
    {
        var unit = await db.BciDevices.FindBySerialAsync(serialNumber);
        if (unit is null)
            return Fail<BciDevice>(ProvisionStatus.NotFound, "Unknown serial number.");
        if (unit.Status == BciDeviceStatus.Manufactured)
            return Fail<BciDevice>(ProvisionStatus.Conflict, "A manufactured unit can't be voided; use rekey to re-flash it.");

        if (unit.Status == BciDeviceStatus.Reserved)
        {
            // A reworked unit awaiting re-confirmation may still have participants paired to it;
            // voiding would strand them.
            if (await db.AccountDevices.AnyAsync(ad => ad.BciDeviceId == unit.Id))
                return Fail<BciDevice>(ProvisionStatus.Conflict,
                    "This unit has participants paired to it and can't be voided; re-flash and confirm it instead.");

            unit.Status = BciDeviceStatus.Voided;
            unit.RegistrationCodeHash = null;
            AddEvent(db, unit, BciDeviceEventType.Voided, userId, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
            logger.LogInformation("Unit {Serial} voided by {UserId}", unit.SerialNumber, userId);
        }

        return new(ProvisionStatus.Ok, unit);
    }

    public async Task<ProvisionResult<IssuedUnit>> RekeyAsync(
        string userId, string? serialNumber, string? reasonInput, string? note, string? firmwareVersion, string? bootloaderVersion)
    {
        // Parsed by hand (not bound as an enum) so an unknown value gets a clear 400 rather than a
        // JSON binding failure, and numeric strings aren't accepted.
        var reasonName = Enum.GetNames<RekeyReason>()
            .FirstOrDefault(n => n.Equals(reasonInput?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (reasonName is null)
            return Fail<IssuedUnit>(ProvisionStatus.BadRequest,
                $"Reason is required: {string.Join(", ", Enum.GetNames<RekeyReason>())}.");
        var reason = Enum.Parse<RekeyReason>(reasonName);
        if (ValidateNote(note) is { } noteError)
            return Fail<IssuedUnit>(ProvisionStatus.BadRequest, noteError);
        if (ValidateFirmware(firmwareVersion) is { } firmwareError)
            return Fail<IssuedUnit>(ProvisionStatus.BadRequest, firmwareError);
        if (ValidateBootloader(bootloaderVersion) is { } bootloaderError)
            return Fail<IssuedUnit>(ProvisionStatus.BadRequest, bootloaderError);

        var unit = await db.BciDevices.FindBySerialAsync(serialNumber);
        if (unit is null)
            return Fail<IssuedUnit>(ProvisionStatus.NotFound, "Unknown serial number.");
        if (unit.Status == BciDeviceStatus.Voided)
            return Fail<IssuedUnit>(ProvisionStatus.Conflict, "This unit's reservation was voided.");

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
            reason: reason, note: note?.Trim(),
            before: unit.CurrentFirmwareVersion, after: firmwareVersion!.Trim(),
            bootloaderBefore: unit.BootloaderVersion, bootloaderAfter: NormalizeBootloader(bootloaderVersion));
        await db.SaveChangesAsync();

        logger.LogInformation("Unit {Serial} re-keyed ({Reason}) by {UserId}", unit.SerialNumber, reason, userId);
        return new(ProvisionStatus.Ok, new IssuedUnit(unit, code));
    }

    /// <summary>
    /// The firmware-write rule: the timestamp always moves; the recorded version only changes when
    /// it actually differs.
    /// </summary>
    public static void ApplyFirmware(BciDevice unit, string version, DateTimeOffset now)
    {
        if (unit.CurrentFirmwareVersion != version)
            unit.CurrentFirmwareVersion = version;
        unit.LastFirmwareUpdatedAt = now;
    }

    /// <summary>
    /// The bootloader rule: a version that's given replaces the recorded one; one that's omitted
    /// leaves it unchanged (so an app update over USB, which never touches the bootloader, can't
    /// erase it). There's no way to clear it: no flow reflashes a bootloader-less image onto a unit
    /// that had one.
    /// </summary>
    public static void ApplyBootloader(BciDevice unit, string? version)
    {
        if (version is not null && unit.BootloaderVersion != version)
            unit.BootloaderVersion = version;
    }

    public static string? NormalizeBootloader(string? version) =>
        string.IsNullOrWhiteSpace(version) ? null : version.Trim();

    public static void AddEvent(
        ApplicationDbContext db,
        BciDevice unit,
        BciDeviceEventType type,
        string userId,
        DateTimeOffset now,
        RekeyReason? reason = null,
        string? note = null,
        string? before = null,
        string? after = null,
        string? bootloaderBefore = null,
        string? bootloaderAfter = null) =>
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
            BootloaderVersionBefore = bootloaderBefore,
            BootloaderVersionAfter = bootloaderAfter,
        });

    /// <summary>Each Validate* returns an error message, or null when the value is acceptable.</summary>
    public static string? ValidateFirmware(string? firmwareVersion)
    {
        var firmware = firmwareVersion?.Trim() ?? string.Empty;
        return firmware.Length is 0 or > 50
            ? "FirmwareVersion is required (max 50 characters)."
            : null;
    }

    public static string? ValidateBootloader(string? version) =>
        version?.Trim() is { Length: > 50 }
            ? "BootloaderVersion is too long (max 50 characters)."
            : null;

    public static string? ValidateNote(string? note) =>
        note is { Length: > MaxNoteLength }
            ? $"Note is too long (max {MaxNoteLength} characters)."
            : null;

    private static ProvisionResult<T> Fail<T>(ProvisionStatus status, string message) => new(status, default, message);
}
