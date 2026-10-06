using System.Globalization;

namespace Intelimensa.Accounts.Firmware;

/// <summary>
/// Intel HEX reader: data, end-of-file and extended-linear-address records, the same subset
/// MindStoneQuarry reads (firmware repo, <c>Intelimensa.Quarry.Core/Identity/IntelHex.cs</c>), so a file
/// accepted here is one the station can read. Every record's framing and checksum is verified and the
/// file must end with an end-of-file record, so a truncated or wrong-format upload is rejected here
/// rather than discovered by a station mid-flash.
/// </summary>
public static class IntelHex
{
    /// <summary>Reads the data records into a sparse address-to-byte map. Throws <see cref="FormatException"/> with a line number.</summary>
    public static Dictionary<uint, byte> Read(string hex)
    {
        var bytes = new Dictionary<uint, byte>();
        uint upper = 0;
        var ended = false;
        var lineNumber = 0;

        foreach (var raw in hex.Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (ended) throw new FormatException($"Line {lineNumber}: data after the end-of-file record.");

            // ':' + count(1) + address(2) + type(1) + data(count) + checksum(1), two hex chars per byte.
            if (line[0] != ':' || line.Length < 11 || line.Length % 2 == 0)
                throw new FormatException($"Line {lineNumber}: not an Intel HEX record.");

            byte[] rec;
            try
            {
                rec = Convert.FromHexString(line.AsSpan(1));
            }
            catch (FormatException)
            {
                throw new FormatException($"Line {lineNumber}: invalid hex digits.");
            }

            int length = rec[0];
            if (rec.Length != length + 5)
                throw new FormatException($"Line {lineNumber}: record length doesn't match its byte count.");
            if (rec.Sum(b => b) % 256 != 0)
                throw new FormatException($"Line {lineNumber}: checksum mismatch.");

            var offset = (ushort)((rec[1] << 8) | rec[2]);
            switch (rec[3])
            {
                case 0x00:
                    for (var i = 0; i < length; i++)
                        bytes[(upper << 16) + offset + (uint)i] = rec[4 + i];
                    break;
                case 0x01:
                    ended = true;
                    break;
                case 0x04:
                    if (length != 2) throw new FormatException($"Line {lineNumber}: bad extended address record.");
                    upper = (uint)((rec[4] << 8) | rec[5]);
                    break;
                default:
                    throw new FormatException($"Line {lineNumber}: unsupported record type {rec[3].ToString("X2", CultureInfo.InvariantCulture)}.");
            }
        }

        if (!ended) throw new FormatException("The file has no end-of-file record (truncated?).");
        if (bytes.Count == 0) throw new FormatException("The file contains no data.");
        return bytes;
    }
}
