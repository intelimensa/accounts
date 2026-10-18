using System.Security.Cryptography;
using System.Text;
using Intelimensa.Accounts.Publishing;

namespace Intelimensa.Accounts.Tests.Publishing;

public class SigningKeyListTests
{
    private const string Statement = "intelimensa-axosync-release-v1\n1.0.0\nWindows\nabc\n1\n";

    private static (string PublicKey, string Signature) NewKey(string statement)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signature = key.SignData(Encoding.UTF8.GetBytes(statement), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), Convert.ToBase64String(signature));
    }

    [Fact]
    public void A_signature_from_any_listed_key_is_accepted()
    {
        var (first, _) = NewKey(Statement);
        var (second, signature) = NewKey(Statement);

        Assert.True(ReleaseSigning.VerifyAny([first, second], Statement, signature));
        Assert.True(ReleaseSigning.VerifyAny([second, first], Statement, signature));
    }

    [Fact]
    public void A_signature_from_an_unlisted_key_is_refused()
    {
        var (listed, _) = NewKey(Statement);
        var (_, strangerSignature) = NewKey(Statement);

        Assert.False(ReleaseSigning.VerifyAny([listed], Statement, strangerSignature));
        Assert.False(ReleaseSigning.VerifyAny([], Statement, strangerSignature));
    }

    [Fact]
    public void A_malformed_key_in_the_list_does_not_hide_a_good_one()
    {
        var (good, signature) = NewKey(Statement);

        Assert.True(ReleaseSigning.VerifyAny(["not base64!", good], Statement, signature));
    }

    [Fact]
    public void Both_settings_are_merged_without_blanks_or_duplicates()
    {
        var options = new ReleasePublishingOptions
        {
            SigningPublicKey = " a ; b ;; ",
            SigningPublicKeys = ["b", "c", " "],
        };

        Assert.Equal(["a", "b", "c"], options.AllSigningPublicKeys);
    }

    [Fact]
    public void No_key_configured_means_signatures_are_not_required()
    {
        Assert.Empty(new ReleasePublishingOptions().AllSigningPublicKeys);
        Assert.Empty(new ReleasePublishingOptions { SigningPublicKey = "  ", SigningPublicKeys = [] }.AllSigningPublicKeys);
    }
}
