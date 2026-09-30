namespace Intelimensa.Accounts.Manufacturing;

/// <summary>
/// Serial-number format <c>PPPP-RVAA-AAAC</c> (14 characters with dashes):
/// <list type="bullet">
/// <item><c>PPPP</c> -- 4-character product code (a readable name like "MSV2");</item>
/// <item><c>R</c> -- regional/hardware variant character (e.g. different line frequency or
/// certification), from the server's allow-list;</item>
/// <item><c>V</c> -- format version, currently always <see cref="FormatVersion"/> ('0'); a parser
/// that sees anything else must treat the serial as an unknown layout, which is what lets the
/// format change later without ambiguity;</item>
/// <item><c>AAAAA</c> -- 5-character base-36 sequence (up to <see cref="MaxSequence"/>);</item>
/// <item><c>C</c> -- check character: Luhn mod 36 over the 11 preceding characters, catching
/// single-character typos and most adjacent transpositions. A typo guard only, no secrecy.</item>
/// </list>
/// Characters are uppercase 0-9/A-Z. Everything except the allocator treats serials as opaque
/// strings, so the layout can evolve; only serials already printed are permanent.
/// </summary>
public static class SerialNumber
{
    public const char FormatVersion = '0';

    public const int SequenceLength = 5;

    /// <summary>36^5 - 1.</summary>
    public const int MaxSequence = 60_466_175;

    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    public static bool IsValidProductCode(string? product) =>
        product is { Length: 4 } && product.All(IsAlphanumeric);

    public static bool IsValidRegionCode(string? region) =>
        region is { Length: 1 } && IsAlphanumeric(region[0]);

    public static string Format(string product, char region, int sequence)
    {
        if (!IsValidProductCode(product))
            throw new ArgumentException("Product code must be 4 uppercase alphanumeric characters.", nameof(product));
        if (!IsAlphanumeric(region))
            throw new ArgumentException("Region code must be 1 uppercase alphanumeric character.", nameof(region));
        if (sequence is < 0 or > MaxSequence)
            throw new ArgumentOutOfRangeException(nameof(sequence));

        var body = $"{product}{region}{FormatVersion}{EncodeSequence(sequence)}"; // 11 chars
        var check = CheckCharacter(body);
        return $"{body[..4]}-{body.Substring(4, 4)}-{body[8..]}{check}";
    }

    /// <summary>
    /// Parses and validates a serial in this format (dashes, alphabet, version and check
    /// character). <paramref name="prefix"/> is product + region + version, i.e. the part a
    /// sequence is counted within.
    /// </summary>
    public static bool TryParse(string? serial, out string prefix, out int sequence)
    {
        prefix = string.Empty;
        sequence = 0;

        if (serial is not { Length: 14 } || serial[4] != '-' || serial[9] != '-')
            return false;

        var body = serial.Remove(9, 1).Remove(4, 1); // 11 body chars + check
        if (!body.All(IsAlphanumeric) || body[5] != FormatVersion)
            return false;
        if (CheckCharacter(body[..11]) != body[11])
            return false;

        prefix = body[..6];
        sequence = DecodeSequence(body.Substring(6, SequenceLength));
        return true;
    }

    private static bool IsAlphanumeric(char c) => char.IsAsciiDigit(c) || char.IsAsciiLetterUpper(c);

    private static string EncodeSequence(int sequence)
    {
        var chars = new char[SequenceLength];
        for (var i = SequenceLength - 1; i >= 0; i--, sequence /= 36)
            chars[i] = Alphabet[sequence % 36];
        return new string(chars);
    }

    private static int DecodeSequence(string encoded)
    {
        var value = 0;
        foreach (var c in encoded)
            value = value * 36 + Alphabet.IndexOf(c);
        return value;
    }

    // Luhn mod N (N = 36), processing right to left with doubling on the rightmost input character.
    private static char CheckCharacter(string input)
    {
        const int n = 36;
        var factor = 2;
        var sum = 0;

        for (var i = input.Length - 1; i >= 0; i--)
        {
            var addend = factor * Alphabet.IndexOf(input[i]);
            factor = factor == 2 ? 1 : 2;
            sum += addend / n + addend % n;
        }

        return Alphabet[(n - sum % n) % n];
    }
}
