using System.Security.Cryptography;
using System.Text;

namespace Intelimensa.Accounts.Security;

/// <summary>
/// The per-unit registration code: 10 characters of Crockford base32 (5 bits each, ~50 bits),
/// generated here, flashed into the unit, printed on its label, and stored only as a SHA-256 hash.
/// A plain hash is enough -- the code is high-entropy random, so there's nothing for a slow KDF to
/// protect (same reasoning as refresh tokens in <see cref="TokenService"/>). See
/// firmware/DEVICE_IDENTITY.md for the device-side contract.
/// </summary>
public static class RegistrationCode
{
    public const int Length = 10;

    // Crockford base32: no I, L, O, U.
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>Generates a code in its canonical form (no dash).</summary>
    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(Length);
        var chars = new char[Length];
        for (var i = 0; i < Length; i++)
            chars[i] = Alphabet[bytes[i] & 0x1F]; // 32 symbols, so masking is unbiased
        return new string(chars);
    }

    /// <summary>Label form, e.g. <c>7KQ4M-9XT2A</c>.</summary>
    public static string ToLabelFormat(string code) => $"{code[..5]}-{code[5..]}";

    /// <summary>
    /// Canonicalizes user/device input: uppercase, drops dashes and whitespace, and applies
    /// Crockford's look-alike substitutions (O to 0, I and L to 1) so a hand-typed label still
    /// matches. Returns null if the result isn't a well-formed code.
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var sb = new StringBuilder(Length);
        foreach (var ch in input.ToUpperInvariant())
        {
            if (ch is '-' or ' ')
                continue;
            sb.Append(ch switch { 'O' => '0', 'I' or 'L' => '1', _ => ch });
        }

        var normalized = sb.ToString();
        return normalized.Length == Length && normalized.All(c => Alphabet.Contains(c)) ? normalized : null;
    }

    public static string Hash(string normalizedCode) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedCode)));

    /// <summary>Constant-time check of submitted input against a stored hash (null hash never matches).</summary>
    public static bool Verify(string? submitted, string? storedHash)
    {
        if (storedHash is null)
            return false;

        var normalized = Normalize(submitted);
        if (normalized is null)
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Hash(normalized)),
            Encoding.UTF8.GetBytes(storedHash));
    }
}
