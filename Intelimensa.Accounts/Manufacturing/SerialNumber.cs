namespace Intelimensa.Accounts.Manufacturing;

/// <summary>
/// Serial-number format <c>PPPR-SSSS-SSSC</c>: 3-character product code, 1-character region code,
/// a 7-digit sequence, and a check character (Luhn mod 36 over the 11 preceding characters, so it
/// catches single-character typos and most adjacent transpositions). The check character is a typo
/// guard only -- it adds no secrecy; the registration code is the credential.
/// </summary>
public static class SerialNumber
{
    public const int MaxSequence = 9_999_999;

    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    public static string Format(string product, char region, int sequence)
    {
        if (product.Length != 3 || !product.All(IsAlphanumeric))
            throw new ArgumentException("Product code must be 3 uppercase alphanumeric characters.", nameof(product));
        if (!IsAlphanumeric(region))
            throw new ArgumentException("Region code must be 1 uppercase alphanumeric character.", nameof(region));
        if (sequence is < 1 or > MaxSequence)
            throw new ArgumentOutOfRangeException(nameof(sequence));

        var body = $"{product}{region}{sequence:D7}";
        var check = CheckCharacter(body);
        return $"{body[..4]}-{body.Substring(4, 4)}-{body[8..]}{check}";
    }

    /// <summary>Parses and validates a serial in this format, including its check character.</summary>
    public static bool TryParse(string? serial, out string prefix, out int sequence)
    {
        prefix = string.Empty;
        sequence = 0;

        if (serial is not { Length: 14 } || serial[4] != '-' || serial[9] != '-')
            return false;

        var body = serial.Remove(9, 1).Remove(4, 1); // PPPRSSSSSSSC
        if (!body[..4].All(IsAlphanumeric) || !body[4..11].All(char.IsAsciiDigit))
            return false;
        if (CheckCharacter(body[..11]) != body[11])
            return false;

        prefix = body[..4];
        sequence = int.Parse(body.AsSpan(4, 7));
        return true;
    }

    private static bool IsAlphanumeric(char c) => char.IsAsciiDigit(c) || char.IsAsciiLetterUpper(c);

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
