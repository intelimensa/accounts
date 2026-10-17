using System.Text.Json;
using Intelimensa.Accounts.Manufacturing;

namespace Intelimensa.Accounts.Tests.Manufacturing;

/// <summary>
/// Runs <see cref="SerialNumber"/> against <c>testdata/serial-number-vectors.json</c>, the fixture shared with
/// MindStoneQuarry's copy of the format (which lets the station reject a mistyped serial before asking this server).
/// This repo owns it (<c>scripts/gen-serial-vectors.py</c>); the Quarry copy must be byte-identical
/// (<c>sync-vectors.sh</c> in the workspace root).
/// </summary>
public class SerialNumberVectorTests
{
    private static readonly JsonElement Doc = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "testdata", "serial-number-vectors.json"))).RootElement;

    public static TheoryData<string> ValidSerials()
    {
        var data = new TheoryData<string>();
        foreach (var v in Doc.GetProperty("valid").EnumerateArray())
            data.Add(v.GetProperty("serial").GetString()!);
        return data;
    }

    public static TheoryData<int> NormalizeCases()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Doc.GetProperty("normalize").GetArrayLength(); i++)
            data.Add(i);
        return data;
    }

    [Fact]
    public void The_sequence_range_is_the_fixtures()
    {
        Assert.Equal(Doc.GetProperty("maxSequence").GetInt32(), SerialNumber.MaxSequence);
    }

    [Theory]
    [MemberData(nameof(ValidSerials))]
    public void A_valid_serial_is_built_parsed_and_shown_as_the_fixture_says(string serial)
    {
        var v = Doc.GetProperty("valid").EnumerateArray().Single(x => x.GetProperty("serial").GetString() == serial);
        var sequence = v.GetProperty("sequence").GetInt32();

        Assert.Equal(serial, SerialNumber.Format(v.GetProperty("product").GetString()!, v.GetProperty("region").GetString()![0], sequence));

        Assert.True(SerialNumber.TryParse(serial, out var prefix, out var parsed));
        Assert.Equal(serial[..6], prefix);
        Assert.Equal(sequence, parsed);

        var display = v.GetProperty("display").GetString()!;
        Assert.Equal(display, SerialNumber.ToDisplay(serial));
        Assert.True(SerialNumber.TryNormalize(display, out var canonical));
        Assert.Equal(serial, canonical);
    }

    [Theory]
    [MemberData(nameof(NormalizeCases))]
    public void TryNormalize_agrees_with_the_fixture(int index)
    {
        var c = Doc.GetProperty("normalize")[index];
        var input = c.GetProperty("input").GetString();
        var expected = c.GetProperty("canonical").GetString();

        Assert.Equal(expected is not null, SerialNumber.TryNormalize(input, out var canonical));
        Assert.Equal(expected ?? string.Empty, canonical);
    }

    [Fact]
    public void Nothing_in_the_invalid_list_is_a_serial()
    {
        foreach (var s in Doc.GetProperty("invalid").EnumerateArray().Select(x => x.GetString()))
        {
            Assert.False(SerialNumber.TryParse(s, out _, out _), s);
            Assert.False(SerialNumber.TryNormalize(s, out _), s);
        }
    }

    [Fact]
    public void A_serial_that_is_not_canonical_is_shown_as_given()
    {
        foreach (var s in Doc.GetProperty("displayUnchanged").EnumerateArray().Select(x => x.GetString()!))
            Assert.Equal(s, SerialNumber.ToDisplay(s));
    }
}
