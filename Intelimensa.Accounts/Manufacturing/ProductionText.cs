namespace Intelimensa.Accounts.Manufacturing;

/// <summary>Shared checks for the free text Manufacturers type into the production pages.</summary>
internal static class ProductionText
{
    /// <summary>An error message, or null. A required value must be non-blank; no control characters at all.</summary>
    public static string? Check(string label, string? value, int max, bool required)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0)
            return required ? $"{label} is required." : null;
        if (text.Length > max)
            return $"{label} can be at most {max} characters.";
        return text.Any(char.IsControl) ? $"{label} can't contain control characters." : null;
    }

    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
