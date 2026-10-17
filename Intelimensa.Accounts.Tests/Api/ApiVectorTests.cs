using System.Text.Json;
using System.Text.Json.Nodes;
using Intelimensa.Accounts.Api.Auth;
using Intelimensa.Accounts.Api.Manufacturing;
using Intelimensa.Accounts.Api.Updates;
using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.Http.Json;

namespace Intelimensa.Accounts.Tests.Api;

/// <summary>
/// Round-trips <c>testdata/api-vectors.json</c> through the server's contract records with the JSON options ASP.NET itself
/// uses: every request in the fixture must be readable into its record, and every response must be written back out exactly
/// (names, casing, nulls kept, enums as numbers, dates). The fixture is shared with MindStoneQuarry's and AxoSync's clients;
/// this repo owns it (<c>scripts/gen-api-vectors.py</c>) and the copies must be byte-identical (<c>sync-vectors.sh</c>).
/// </summary>
public class ApiVectorTests
{
    private static readonly JsonElement Doc = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "testdata", "api-vectors.json"))).RootElement;

    // what minimal APIs use when Program.cs doesn't configure anything (it doesn't: it has no JSON settings)
    private static readonly JsonSerializerOptions Options = new JsonOptions().SerializerOptions;

    private static readonly Dictionary<string, Type> Records = new()
    {
        ["LoginRequest"] = typeof(LoginRequest),
        ["RefreshRequest"] = typeof(RefreshRequest),
        ["TokenResponse"] = typeof(TokenResponse),
        ["ManufacturingOptionsResponse"] = typeof(ManufacturingOptionsResponse),
        ["ReserveUnitRequest"] = typeof(ReserveUnitRequest),
        ["IssuedUnitResponse"] = typeof(IssuedUnitResponse),
        ["ConfirmUnitRequest"] = typeof(ConfirmUnitRequest),
        ["UnitRequest"] = typeof(UnitRequest),
        ["RekeyUnitRequest"] = typeof(RekeyUnitRequest),
        ["FirmwareWriteRequest"] = typeof(FirmwareWriteRequest),
        ["UnitResponse"] = typeof(UnitResponse),
        ["UpdateCheckResponse"] = typeof(UpdateCheckResponse),
    };

    public static TheoryData<string> ExampleNames()
    {
        var data = new TheoryData<string>();
        foreach (var e in Doc.GetProperty("examples").EnumerateArray())
            data.Add(e.GetProperty("name").GetString()!);
        return data;
    }

    [Fact]
    public void Every_type_in_the_fixture_is_a_record_the_server_has()
    {
        foreach (var e in Doc.GetProperty("examples").EnumerateArray())
            Assert.True(Records.ContainsKey(e.GetProperty("type").GetString()!), e.GetProperty("name").GetString());
    }

    [Theory]
    [MemberData(nameof(ExampleNames))]
    public void The_example_is_read_and_written_as_the_fixture_says(string name)
    {
        var example = Doc.GetProperty("examples").EnumerateArray().Single(e => e.GetProperty("name").GetString() == name);
        var type = Records[example.GetProperty("type").GetString()!];
        var wire = JsonNode.Parse(example.GetProperty("json").GetRawText())!;

        var value = JsonSerializer.Deserialize(example.GetProperty("json").GetRawText(), type, Options);
        Assert.NotNull(value);
        var written = JsonSerializer.SerializeToNode(value, type, Options)!;

        if (example.GetProperty("direction").GetString() == "response")
            Assert.True(JsonNode.DeepEquals(wire, written), $"the server writes\n{written}\nnot\n{wire}");
        else
            // the server accepts a request with optional fields left out, and writes them back as null
            Assert.True(JsonNode.DeepEquals(WithoutNulls(wire), WithoutNulls(written)), $"the server reads\n{written}\nfrom\n{wire}");
    }

    [Fact]
    public void The_rekey_reasons_a_station_may_send_are_the_servers()
    {
        var offered = Enum.GetNames<RekeyReason>().Where(n => n != nameof(RekeyReason.Initial));
        Assert.Equal(offered, Doc.GetProperty("rekeyReasons").EnumerateArray().Select(r => r.GetString()));
    }

    private static JsonNode? WithoutNulls(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                var copy = new JsonObject();
                foreach (var (key, value) in o.Where(p => p.Value is not null))
                    copy[key] = WithoutNulls(value);
                return copy;
            case JsonArray a:
                return new JsonArray(a.Select(WithoutNulls).ToArray());
            default:
                return node?.DeepClone();
        }
    }
}
