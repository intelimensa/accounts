namespace Intelimensa.Accounts.Models;

public enum BciDeviceEventType
{
    Reserved = 0,
    Confirmed = 1,
    Voided = 2,
    Rekeyed = 3,
    FirmwareWritten = 4,
}

/// <summary>
/// Why a unit's registration code was rotated. Every rekey invalidates the old code and requires
/// the identity page to be re-flashed; the reason records what prompted it.
/// </summary>
public enum RekeyReason
{
    /// <summary>The label was lost, leaked or damaged. The plaintext code isn't stored, so it can't just be reprinted.</summary>
    Relabel = 1,

    /// <summary>The identity was lost or corrupted (a programmer flash erases it) and couldn't be restored. A reflash that puts the same identity back needs no rekey.</summary>
    Reflash = 2,

    /// <summary>Hardware rework or a board swap.</summary>
    Rework = 3,
}

/// <summary>
/// Append-only history of a <see cref="BciDevice"/>'s manufacturing lifecycle, written in the same
/// transaction as the state change it describes. Never contains a registration code.
/// </summary>
public class BciDeviceEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid BciDeviceId { get; set; }

    public BciDevice? BciDevice { get; set; }

    public BciDeviceEventType Type { get; set; }

    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;

    public string? UserId { get; set; }

    /// <summary>Set for <see cref="BciDeviceEventType.Rekeyed"/> events.</summary>
    public RekeyReason? Reason { get; set; }

    public string? Note { get; set; }

    /// <summary>The unit's recorded firmware version before this event.</summary>
    public string? FirmwareVersionBefore { get; set; }

    /// <summary>
    /// The firmware version this event set -- or, for <see cref="BciDeviceEventType.Rekeyed"/>, the
    /// version the station said it intends to flash (applied only at confirm).
    /// </summary>
    public string? FirmwareVersionAfter { get; set; }

    /// <summary>The unit's recorded bootloader version before this event (null if none/unknown).</summary>
    public string? BootloaderVersionBefore { get; set; }

    /// <summary>
    /// The bootloader version this event left the unit with -- or, for
    /// <see cref="BciDeviceEventType.Reserved"/> and <see cref="BciDeviceEventType.Rekeyed"/>, the
    /// version the station said it intends to flash (applied only at confirm).
    /// </summary>
    public string? BootloaderVersionAfter { get; set; }
}
