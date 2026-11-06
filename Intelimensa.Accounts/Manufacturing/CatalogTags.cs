using System.Text.RegularExpressions;

namespace Intelimensa.Accounts.Manufacturing;

/// <summary>
/// The rules for tags: 1 to 40 lowercase letters, digits, '-' or '_' (typed in any case, stored lowercase), at most 20 per item,
/// written as a comma-separated list.
/// </summary>
public static partial class CatalogTags
{
    public const int MaxLength = 40;
    public const int MaxPerItem = 20;

    public const string Rule = "A tag is 1 to 40 letters, digits, '-' or '_'.";

    [GeneratedRegex("^[a-z0-9_-]{1,40}$")]
    private static partial Regex Valid();

    /// <summary>True for a tag in its stored form (already lowercase and trimmed).</summary>
    public static bool IsValid(string tag) => Valid().IsMatch(tag);

    /// <summary>Trims and lowercases a single tag; false if it is not valid.</summary>
    public static bool TryNormalize(string? input, out string tag)
    {
        tag = input?.Trim().ToLowerInvariant() ?? string.Empty;
        return Valid().IsMatch(tag);
    }

    /// <summary>
    /// Splits a comma-separated list: trims, lowercases, drops blanks and repeats, and returns the tags sorted. The error is
    /// null when every tag is valid and there are at most <see cref="MaxPerItem"/>.
    /// </summary>
    public static (List<string> Tags, string? Error) ParseList(string? text)
    {
        var tags = new List<string>();
        foreach (var piece in (text ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryNormalize(piece, out var tag))
                return ([], $"'{piece}' is not a valid tag. {Rule}");
            if (!tags.Contains(tag))
                tags.Add(tag);
        }
        if (tags.Count > MaxPerItem)
            return ([], $"An item can have at most {MaxPerItem} tags.");
        tags.Sort(StringComparer.Ordinal);
        return (tags, null);
    }

    /// <summary>The list as shown and exported: sorted, comma-separated (no spaces).</summary>
    public static string Format(IEnumerable<string> tags) => string.Join(",", tags.Order(StringComparer.Ordinal));
}
