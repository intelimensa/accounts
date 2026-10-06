using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace Intelimensa.Accounts.Firmware;

/// <summary>What kind of image a HEX holds.</summary>
public enum FirmwareImageKind
{
    /// <summary>One program, no bootloader (MS-V1/MS-V2). Its version is found by searching the image.</summary>
    Single,

    /// <summary>MS-V3 bootloader + app, for a programmer (the firmware repo's <c>msv3-factory.hex</c>).</summary>
    MsV3Factory,

    /// <summary>An MS-V3 app on its own: what an update over USB sends. No bootloader, so a programmer can't use it alone.</summary>
    MsV3App,
}

/// <param name="Version">The app's version (the unit's firmware version), or null if none was found.</param>
/// <param name="BootloaderVersion">The MS-V3 bootloader's version in a factory image, or null.</param>
public sealed record FirmwareDescription(FirmwareImageKind Kind, string? Version, string? BootloaderVersion);

/// <summary>The HEX isn't a firmware image the station could use.</summary>
public sealed class FirmwareImageException(string message) : Exception(message);

/// <summary>
/// Reads what the catalog needs to know about a firmware HEX without anyone typing it. This is a port of
/// MindStoneQuarry's <c>FirmwareHex.Describe</c> and the MS-V3 header check of its <c>AppImage</c> (firmware
/// repo, <c>Intelimensa.Quarry.Core/Identity/FirmwareHex.cs</c> and <c>Update/AppImage.cs</c>), so the server
/// and the station agree on a file's version. If the firmware's image layout changes, change both.
/// </summary>
public static partial class FirmwareImage
{
    // physical addresses (pic32/MS-V3/src/boot_iface.h)
    private const uint BootloaderStart = 0x1D000000;
    private const uint BootloaderEnd = 0x1D004000;

    // MS-V3 app, kseg0 addresses as the bootloader protocol uses them
    private const uint KsegMask = 0x1FFFFFFF;       // kseg0 address -> physical (HEX) address
    private const uint HeaderAddress = 0x9D004000;
    private const uint ImageStart = 0x9D004400;
    private const uint AppEnd = 0x9D01EC00;         // identity page
    private const uint Magic = 0x3356534D;          // "MSV3"
    private const uint Format = 1;
    private const int HeaderSize = 56;              // magic, format, entry, length, crc32, version[32], header_crc
    private const int HeaderCrcOffset = 52;

    // FIRMWARE_VERSION is a NUL-terminated string in flash: MAJOR.HEIGHT+shorthash, plus .dirty for an unclean tree
    [GeneratedRegex(@"(?<![0-9A-Za-z.+])(\d+\.\d+\+[0-9a-f]{7,40}(?:\.dirty)?)\0")]
    private static partial Regex VersionString();

    [GeneratedRegex(@"^\d+\.\d+\+[0-9a-f]{7,40}$")]
    private static partial Regex ReleaseVersion();

    /// <summary>
    /// True for a release version, MAJOR.HEIGHT+hash: built from a clean tree, not a test build. Only those
    /// identify what's on a unit, so only those go into the catalog (the station refuses the others too).
    /// Also the only form that is safe to use in a storage path.
    /// </summary>
    public static bool IsRelease(string version) => ReleaseVersion().IsMatch(version);

    /// <summary>
    /// What the image is and its versions. An MS-V3 app's version comes from its header, which must check
    /// out (header and image CRCs, as the bootloader checks them). Throws <see cref="FirmwareImageException"/>
    /// for something that isn't a readable HEX or has a bad MS-V3 header.
    /// </summary>
    public static FirmwareDescription Describe(string hex)
    {
        Dictionary<uint, byte> bytes;
        try
        {
            bytes = IntelHex.Read(hex);
        }
        catch (FormatException ex)
        {
            throw new FirmwareImageException($"Not a readable HEX file. {ex.Message}");
        }

        if (!HasHeaderMagic(bytes))
            return new FirmwareDescription(FirmwareImageKind.Single, SearchVersion(bytes), null);

        var appVersion = ReadAppVersion(bytes);
        var bootloader = bytes.Where(kv => kv.Key is >= BootloaderStart and < BootloaderEnd);
        return bootloader.Any()
            ? new FirmwareDescription(FirmwareImageKind.MsV3Factory, appVersion, SearchVersion(bootloader))
            : new FirmwareDescription(FirmwareImageKind.MsV3App, appVersion, null);
    }

    private static bool HasHeaderMagic(Dictionary<uint, byte> bytes)
    {
        var a = HeaderAddress & KsegMask;
        return bytes.TryGetValue(a, out var b0) && b0 == 0x4D && bytes.TryGetValue(a + 1, out var b1) && b1 == 0x53
            && bytes.TryGetValue(a + 2, out var b2) && b2 == 0x56 && bytes.TryGetValue(a + 3, out var b3) && b3 == 0x33;
    }

    /// <summary>The app's version string from its header, after the checks the bootloader will make.</summary>
    private static string ReadAppVersion(Dictionary<uint, byte> flash)
    {
        byte[] Range(uint address, int count)
        {
            var data = new byte[count];
            var physical = address & KsegMask;
            for (var i = 0; i < count; i++)
                data[i] = flash.TryGetValue(physical + (uint)i, out var b) ? b : (byte)0xFF;
            return data;
        }

        var header = Range(HeaderAddress, HeaderSize);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic
            || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) != Format)
            throw new FirmwareImageException("The HEX has no MS-V3 app header.");
        if (Crc32(header.AsSpan(0, HeaderCrcOffset)) != BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(HeaderCrcOffset)))
            throw new FirmwareImageException("The app header's CRC is wrong (built without msv3_app_header.py?).");

        var entry = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        var imageCrc = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
        if (length == 0 || length > AppEnd - ImageStart || entry < ImageStart || entry >= ImageStart + length)
            throw new FirmwareImageException("The app header's length or entry address is out of range.");
        if (Crc32(Range(ImageStart, (int)length)) != imageCrc)
            throw new FirmwareImageException("The app image doesn't match its header's CRC.");

        return Encoding.ASCII.GetString(header, 20, 32).Split('\0')[0];
    }

    /// <summary>The one version string in the given bytes, or null if there is none or several different ones.</summary>
    private static string? SearchVersion(IEnumerable<KeyValuePair<uint, byte>> bytes)
    {
        // the image as text, with a NUL wherever the address space has a gap
        var text = new StringBuilder();
        uint? previous = null;
        foreach (var (address, value) in bytes.OrderBy(kv => kv.Key))
        {
            if (previous is { } p && address != p + 1)
                text.Append('\0');
            text.Append(value is >= 0x20 and < 0x7F ? (char)value : '\0');
            previous = address;
        }

        var found = VersionString().Matches(text.ToString()).Select(m => m.Groups[1].Value).Distinct().ToList();
        return found.Count == 1 ? found[0] : null;
    }

    /// <summary>Standard CRC-32 (IEEE 802.3 / zlib): poly 0xEDB88320, init and final xor 0xFFFFFFFF.</summary>
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc >> 1) ^ (0xEDB88320u & (0u - (crc & 1u)));
        }
        return ~crc;
    }
}
