using System.Text.Json;
using Intelimensa.Accounts.Security;

namespace Intelimensa.Accounts.Tests.Security;

/// <summary>
/// Checks <see cref="RegistrationCode"/> against the registration codes and the alphabet of
/// <c>testdata/identity-vectors.json</c>, the fixture for what is stored in a unit and sent by <c>dcGetIdentity</c>. The
/// firmware repo owns it (<c>testdata/gen-identity-vectors.py</c>); this copy must be byte-identical (<c>sync-vectors.sh</c>
/// in the workspace root). The server generates the code, the unit stores it, and Quarry reads it back from the unit and
/// sends it to <c>confirm</c>, so all three must mean the same alphabet.
/// </summary>
public class RegistrationCodeVectorTests
{
    private static readonly JsonElement Doc = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "testdata", "identity-vectors.json"))).RootElement;

    private static string Alphabet => Doc.GetProperty("codeAlphabet").GetString()!;

    [Fact]
    public void Every_character_of_the_alphabet_is_a_code_character()
    {
        foreach (var c in Alphabet)
            Assert.Equal(new string(c, RegistrationCode.Length), RegistrationCode.Normalize(new string(c, RegistrationCode.Length)));
    }

    [Fact]
    public void Characters_outside_the_alphabet_are_refused_except_the_look_alikes_it_folds()
    {
        // O, I and L are folded onto 0 and 1 for a hand-typed label, on purpose; U and everything else is refused
        foreach (var c in "UuÜ!$_#@*&%+=/")
            Assert.Null(RegistrationCode.Normalize(new string(c, RegistrationCode.Length)));
        Assert.Equal("0000000000", RegistrationCode.Normalize("OOOOOOOOOO"));
        Assert.Equal("1111111111", RegistrationCode.Normalize("ILIiLl1ILl"));
    }

    [Fact]
    public void Generated_codes_use_only_the_alphabet_and_all_of_it()
    {
        var seen = new HashSet<char>();
        for (var i = 0; i < 2000; i++)
        {
            var code = RegistrationCode.Generate();
            Assert.Equal(RegistrationCode.Length, code.Length);
            foreach (var c in code)
            {
                Assert.Contains(c, Alphabet);
                seen.Add(c);
            }
        }
        Assert.Equal(Alphabet.Length, seen.Count);
    }

    [Fact]
    public void The_codes_stored_in_the_fixtures_records_are_valid_codes_in_their_label_form_too()
    {
        foreach (var record in Doc.GetProperty("records").EnumerateArray().Where(r => r.TryGetProperty("build", out _)))
        {
            var code = record.GetProperty("build").GetProperty("code").GetString()!;
            Assert.Equal(code, RegistrationCode.Normalize(code));
            Assert.Equal(code, RegistrationCode.Normalize(RegistrationCode.ToLabelFormat(code)));
            Assert.Equal(code, RegistrationCode.Normalize(RegistrationCode.ToLabelFormat(code).ToLowerInvariant()));
        }
    }
}
