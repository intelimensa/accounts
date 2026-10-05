namespace Intelimensa.Accounts.Firmware;

/// <summary>
/// Structural check for Intel HEX files, so a truncated or wrong-format upload is rejected at the
/// door rather than discovered by a station mid-flash. Checks record framing, every record's
/// checksum and the terminating end-of-file record; it knows nothing about the target device.
/// </summary>
public static class IntelHex
{
    private const int EndOfFileRecord = 0x01;

    /// <summary>Returns null if the content is a well-formed hex file, otherwise a staff-facing reason.</summary>
    public static string? Validate(Stream content)
    {
        using var reader = new StreamReader(content, System.Text.Encoding.ASCII);
        var lineNumber = 0;
        var records = 0;
        var sawEof = false;

        while (reader.ReadLine() is { } raw)
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (sawEof) return $"Line {lineNumber}: data after the end-of-file record.";

            // ':' + count(1) + address(2) + type(1) + data(count) + checksum(1), two hex chars per byte.
            if (line[0] != ':' || line.Length < 11 || line.Length % 2 == 0)
                return $"Line {lineNumber}: not an Intel HEX record.";

            var bytes = new byte[(line.Length - 1) / 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                if (!byte.TryParse(line.AsSpan(1 + i * 2, 2), System.Globalization.NumberStyles.HexNumber,
                        null, out bytes[i]))
                    return $"Line {lineNumber}: invalid hex digits.";
            }

            if (bytes.Length != bytes[0] + 5)
                return $"Line {lineNumber}: record length doesn't match its byte count.";
            if (bytes.Sum(b => b) % 256 != 0)
                return $"Line {lineNumber}: checksum mismatch.";

            records++;
            if (bytes[3] == EndOfFileRecord) sawEof = true;
        }

        if (records == 0) return "The file contains no hex records.";
        if (!sawEof) return "The file has no end-of-file record (truncated?).";
        return null;
    }
}
