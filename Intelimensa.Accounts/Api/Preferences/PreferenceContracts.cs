namespace Intelimensa.Accounts.Api.Preferences;

/// <summary>What a slot holds: <c>V</c> is the payload format version, <c>Values</c> AxoSync's sparse overrides (absent key = config default).</summary>
public record PreferencePayload(int V, Dictionary<string, string> Values);

public record PreferenceSlotSummary(int Slot, string Name, long Version, DateTimeOffset? UpdatedAt, bool Empty);

public record PreferenceSlotListResponse(List<PreferenceSlotSummary> Slots);

public record PreferenceSlotResponse(int Slot, string Name, long Version, DateTimeOffset? UpdatedAt, PreferencePayload Payload);

public record PutPreferenceSlotRequest(string Name, PreferencePayload Payload);

public record RenamePreferenceSlotRequest(string Name);

/// <summary>The 412 body: the slot as it is now, so the client can merge and retry without another GET.</summary>
public record PreferenceSlotConflictResponse(string Error, PreferenceSlotResponse Current);
