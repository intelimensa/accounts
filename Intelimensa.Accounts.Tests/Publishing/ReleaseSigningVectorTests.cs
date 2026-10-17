using System.Text;
using System.Text.Json;
using Intelimensa.Accounts.Models;
using Intelimensa.Accounts.Publishing;

namespace Intelimensa.Accounts.Tests.Publishing;

/// <summary>
/// Runs <see cref="ReleaseSigning"/> against <c>testdata/release-signing-vectors.json</c>, the fixture shared with
/// AxoSync's UpdateTrust and publish.sh. Those three implement one contract; this is what stops them drifting.
/// </summary>
public class ReleaseSigningVectorTests
{
    private static readonly JsonElement Doc = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "testdata", "release-signing-vectors.json"))).RootElement;

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in Doc.GetProperty("cases").EnumerateArray())
            data.Add(c.GetProperty("name").GetString()!);
        return data;
    }

    private static JsonElement Case(string name) =>
        Doc.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    [Fact]
    public void Fixture_covers_every_platform()
    {
        var platforms = Doc.GetProperty("cases").EnumerateArray()
            .Where(c => c.GetProperty("expectValid").GetBoolean())
            .Select(c => c.GetProperty("platform").GetString())
            .ToHashSet();
        foreach (var platform in Enum.GetNames<ReleasePlatform>())
            Assert.Contains(platform, platforms);
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Statement_matches_the_signed_bytes(string name)
    {
        var c = Case(name);
        var statement = ReleaseSigning.Statement(
            c.GetProperty("version").GetString()!,
            Enum.Parse<ReleasePlatform>(c.GetProperty("platform").GetString()!),
            c.GetProperty("sha256").GetString()!,
            c.GetProperty("sizeBytes").GetInt64());

        var signedHex = c.GetProperty("signedStatementHex").GetString()!;
        var matches = Convert.ToHexString(Encoding.UTF8.GetBytes(statement)).Equals(signedHex, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(c.GetProperty("statementMatchesFields").GetBoolean(), matches);
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Verify_agrees_with_the_fixture(string name)
    {
        var c = Case(name);
        var statement = ReleaseSigning.Statement(
            c.GetProperty("version").GetString()!,
            Enum.Parse<ReleasePlatform>(c.GetProperty("platform").GetString()!),
            c.GetProperty("sha256").GetString()!,
            c.GetProperty("sizeBytes").GetInt64());

        var valid = ReleaseSigning.Verify(
            Doc.GetProperty("publicKey").GetString()!, statement, c.GetProperty("signature").GetString()!);

        Assert.Equal(c.GetProperty("expectValid").GetBoolean(), valid);
    }
}
