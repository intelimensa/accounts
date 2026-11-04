using System.Text.Json;
using Intelimensa.Accounts.Api.Preferences;

namespace Intelimensa.Accounts.Preferences;

/// <summary>
/// What a preference slot may hold. The server knows no key, so these are the only rules: sizes, and no control
/// characters (AxoSync's local file is one <c>key = value</c> line each, which a newline in a value would break).
/// </summary>
public static class PreferenceLimits
{
    public const int SlotCount = 5;
    public const int MaxNameLength = 40;
    public const int MaxKeys = 256;
    public const int MaxKeyLength = 64;
    public const int MaxValueLength = 1024;
    public const int MaxPayloadBytes = 32 * 1024;

    public static bool IsSlot(int slot) => slot is >= 1 and <= SlotCount;

    public static string DefaultName(int slot) => $"Profile {slot}";

    /// <summary>The name trimmed, or null with <paramref name="error"/> set.</summary>
    public static string? NormalizeName(string? name, out string? error)
    {
        var trimmed = name?.Trim();
        error = null;
        if (string.IsNullOrEmpty(trimmed))
            error = "The name is required.";
        else if (trimmed.Length > MaxNameLength)
            error = $"The name is too long (at most {MaxNameLength} characters).";
        else if (trimmed.Any(char.IsControl))
            error = "The name contains control characters.";
        return error is null ? trimmed : null;
    }

    /// <summary>Null when the payload is acceptable, otherwise a sentence for a person to read.</summary>
    public static string? ValidatePayload(PreferencePayload? payload)
    {
        if (payload?.Values is null)
            return "The payload is required.";
        if (payload.V < 1)
            return "The payload format version must be 1 or more.";
        if (payload.Values.Count > MaxKeys)
            return $"Too many values (at most {MaxKeys}).";

        foreach (var (key, value) in payload.Values)
        {
            if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength)
                return $"A key must be 1 to {MaxKeyLength} characters.";
            if (key.Any(char.IsControl))
                return "A key contains control characters.";
            if (value is null)
                return $"The value of {key} is missing.";
            if (value.Length > MaxValueLength)
                return $"The value of {key} is too long (at most {MaxValueLength} characters).";
            if (value.Any(char.IsControl))
                return $"The value of {key} contains control characters.";
        }

        return JsonSerializer.SerializeToUtf8Bytes(payload).Length > MaxPayloadBytes
            ? $"The payload is too large (at most {MaxPayloadBytes / 1024} KiB)."
            : null;
    }

    /// <summary>The payload as stored: keys in ordinal order, so equal content is equal text.</summary>
    public static string Canonical(PreferencePayload payload) => JsonSerializer.Serialize(new PreferencePayload(
        payload.V, payload.Values.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value)));
}
