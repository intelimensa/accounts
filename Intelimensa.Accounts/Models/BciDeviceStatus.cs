namespace Intelimensa.Accounts.Models;

/// <summary>
/// Where a <see cref="BciDevice"/> is in its manufacturing lifecycle. Only
/// <see cref="Manufactured"/> units can be registered by participants.
/// </summary>
public enum BciDeviceStatus
{
    /// <summary>
    /// A serial and registration code have been issued to a manufacturing station but the unit
    /// hasn't been confirmed as flashed and verified yet (or its code was rotated and it's waiting
    /// to be re-flashed).
    /// </summary>
    Reserved = 0,

    /// <summary>Flashed, read back and confirmed -- or a legacy/backfilled unit with no code.</summary>
    Manufactured = 1,

    /// <summary>A reservation abandoned before the unit was confirmed (e.g. a failed flash).</summary>
    Voided = 2,
}
