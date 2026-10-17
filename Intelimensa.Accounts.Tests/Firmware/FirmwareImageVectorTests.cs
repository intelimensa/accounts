using System.Text.Json;
using Intelimensa.Accounts.Firmware;

namespace Intelimensa.Accounts.Tests.Firmware;

/// <summary>
/// Runs <see cref="FirmwareImage.Describe"/> against <c>testdata/firmware-image-vectors.json</c>, the fixture
/// shared with MindStoneQuarry's reader. The firmware repo owns it (<c>quarry/tests/testdata/</c>); this copy
/// must be byte-identical (<c>sync-vectors.sh</c> in the workspace root). The catalog may be stricter than
/// Quarry (a case then has <c>accountsExpect</c>) but must never accept what Quarry rejects, because Quarry
/// flashes only what the catalog holds.
/// </summary>
public class FirmwareImageVectorTests
{
    private static readonly JsonElement Doc = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "testdata", "firmware-image-vectors.json"))).RootElement;

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in Doc.GetProperty("cases").EnumerateArray())
            data.Add(c.GetProperty("name").GetString()!);
        return data;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Describe_agrees_with_the_fixture(string name)
    {
        var c = Doc.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        var expect = c.TryGetProperty("accountsExpect", out var stricter) ? stricter : c.GetProperty("expect");
        var hex = c.GetProperty("hex").GetString()!;

        if (expect.GetProperty("outcome").GetString() == "rejected")
        {
            // the upload page shows FirmwareImageException's message; nothing else may leak out of Describe
            Assert.Throws<FirmwareImageException>(() => FirmwareImage.Describe(hex));
            return;
        }

        var d = FirmwareImage.Describe(hex);
        Assert.Equal(expect.GetProperty("kind").GetString(), d.Kind.ToString());
        Assert.Equal(Str(expect, "version"), d.Version);
        Assert.Equal(Str(expect, "bootloaderVersion"), d.BootloaderVersion);
        if (d.Version is not null)
            Assert.Equal(expect.GetProperty("versionIsRelease").GetBoolean(), FirmwareImage.IsRelease(d.Version));
    }

    [Fact]
    public void The_catalog_never_accepts_what_Quarry_rejects()
    {
        foreach (var c in Doc.GetProperty("cases").EnumerateArray()
                     .Where(x => x.GetProperty("expect").GetProperty("outcome").GetString() == "rejected"))
        {
            Assert.False(c.TryGetProperty("accountsExpect", out _), $"{c.GetProperty("name")}: accountsExpect can only make the catalog stricter");
            Assert.Throws<FirmwareImageException>(() => FirmwareImage.Describe(c.GetProperty("hex").GetString()!));
        }
    }

    private static string? Str(JsonElement e, string property) =>
        e.GetProperty(property).ValueKind == JsonValueKind.Null ? null : e.GetProperty(property).GetString();
}
