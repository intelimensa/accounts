using System.Text.RegularExpressions;

namespace Intelimensa.Accounts.Publishing;

/// <summary>
/// A semantic version (<c>1.3.0</c>, <c>1.4.0-beta.1</c>) with SemVer 2.0 precedence: numeric parts
/// first, then a version with a pre-release tag sorts below the same version without one. Build
/// metadata (<c>+sha</c>) is not accepted -- a release version never carries it.
/// </summary>
public readonly partial record struct ReleaseVersion : IComparable<ReleaseVersion>
{
    [GeneratedRegex(@"^(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z]+(?:\.[0-9A-Za-z]+)*))?$")]
    private static partial Regex Pattern();

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string? PreRelease { get; }

    private ReleaseVersion(int major, int minor, int patch, string? preRelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
    }

    public bool IsPreRelease => PreRelease is not null;

    /// <summary>Accepts a build-metadata suffix on <paramref name="text"/> and drops it, for client-reported versions.</summary>
    public static bool TryParse(string? text, bool allowBuildMetadata, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        text = text.Trim();
        if (allowBuildMetadata && text.IndexOf('+') is var plus and >= 0)
            text = text[..plus];

        var match = Pattern().Match(text);
        if (!match.Success) return false;
        if (!int.TryParse(match.Groups[1].Value, out var major) ||
            !int.TryParse(match.Groups[2].Value, out var minor) ||
            !int.TryParse(match.Groups[3].Value, out var patch))
            return false;

        version = new ReleaseVersion(major, minor, patch, match.Groups[4].Success ? match.Groups[4].Value : null);
        return true;
    }

    public static bool TryParse(string? text, out ReleaseVersion version) => TryParse(text, false, out version);

    public int CompareTo(ReleaseVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;

        if (PreRelease is null) return other.PreRelease is null ? 0 : 1;
        if (other.PreRelease is null) return -1;

        var left = PreRelease.Split('.');
        var right = other.PreRelease.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var leftNumeric = IsNumeric(left[i]);
            var rightNumeric = IsNumeric(right[i]);
            c = (leftNumeric, rightNumeric) switch
            {
                (true, true) => CompareNumeric(left[i], right[i]),
                (true, false) => -1,   // numeric identifiers sort below alphanumeric ones
                (false, true) => 1,
                _ => string.CompareOrdinal(left[i], right[i]),
            };
            if (c != 0) return c;
        }
        return left.Length.CompareTo(right.Length);
    }

    private static bool IsNumeric(string identifier) => identifier.All(char.IsAsciiDigit);

    /// <summary>Numeric identifiers of any length: by digit count (ignoring leading zeros), then digit by digit.</summary>
    private static int CompareNumeric(string a, string b)
    {
        a = a.TrimStart('0');
        b = b.TrimStart('0');
        var c = a.Length.CompareTo(b.Length);
        return c != 0 ? c : string.CompareOrdinal(a, b);
    }

    public static bool operator <(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() =>
        $"{Major}.{Minor}.{Patch}" + (PreRelease is null ? "" : $"-{PreRelease}");
}
