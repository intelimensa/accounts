namespace Intelimensa.Accounts.Models;

/// <summary>
/// One of an <see cref="Account"/>'s five named preference profiles (docs/preferences-and-game-results.md). The payload is
/// AxoSync's own sparse map of overrides, stored as the JSON text the server wrote: the server checks its shape and limits
/// (<c>PreferenceLimits</c>) but knows no key. A slot that was never written has no row; the API reports it as empty at
/// version 0. Keyed by (<see cref="AccountId"/>, <see cref="Slot"/>).
/// </summary>
public class PreferenceSlot
{
    public int AccountId { get; set; }

    public Account? Account { get; set; }

    /// <summary>1 to <c>PreferenceLimits.SlotCount</c>.</summary>
    public int Slot { get; set; }

    public required string Name { get; set; }

    /// <summary>The canonical JSON of the payload: <c>{"v":1,"values":{...}}</c>.</summary>
    public required string Payload { get; set; }

    /// <summary>Starts at 1, +1 on every successful write; the ETag, and the concurrency token.</summary>
    public long Version { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
