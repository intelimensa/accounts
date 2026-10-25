using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Intelimensa.Accounts.ConfigDelivery;
using Intelimensa.Accounts.Security;

namespace Intelimensa.Accounts.Tests.ConfigDelivery;

/// <summary>
/// Runs the server's install proofs, CEK wrap, config blob and offline lease against
/// <c>testdata/config-delivery-vectors.json</c> (this repo owns it; <c>scripts/gen-config-delivery-vectors.py</c>, an
/// implementation independent of .NET's). AxoSync and ConfigGen test their sides against copies of the same file.
/// </summary>
public class ConfigDeliveryVectorTests
{
    private static readonly JsonElement Doc = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "testdata", "config-delivery-vectors.json"))).RootElement;

    private static JsonElement Section(string name) => Doc.GetProperty(name);

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    private static byte[] B64(JsonElement e, string name) => Convert.FromBase64String(Str(e, name));

    public static TheoryData<string> Names(string section, string list)
    {
        var data = new TheoryData<string>();
        foreach (var c in Section(section).GetProperty(list).EnumerateArray())
            data.Add(c.GetProperty("name").GetString()!);
        return data;
    }

    private static JsonElement Case(string section, string list, string name) =>
        Section(section).GetProperty(list).EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    /// <summary>A private key from the fixture (hex scalar), its public half taken from the SPKI next to it.</summary>
    private static ECParameters PrivateKey(JsonElement key)
    {
        var spki = B64(key, "publicKey");
        return new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Convert.FromHexString(Str(key, "privateKey")),
            Q = new ECPoint { X = spki[^64..^32], Y = spki[^32..] },
        };
    }

    // ---- install proofs ----------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Names), "installProof", "cases")]
    public void Install_proof(string name)
    {
        var c = Case("installProof", "cases", name);
        var body = Encoding.UTF8.GetBytes(Str(c, "body"));

        Assert.Equal(Str(c, "bodySha256"), InstallProof.BodySha256(body));
        var statement = InstallProof.Statement(Str(c, "method"), Str(c, "pathAndQuery"), InstallProof.BodySha256(body), Str(c, "nonce"));
        Assert.Equal(Str(c, "statement"), statement);
        Assert.Equal(c.GetProperty("valid").GetBoolean(), InstallProof.Verify(Str(c, "publicKey"), statement, Str(c, "signature")));
    }

    [Fact]
    public void Install_keys_are_accepted_and_their_thumbprint_is_the_hash_of_the_der()
    {
        var key = Section("installProof").GetProperty("key");
        Assert.True(P256.IsPublicKey(Str(key, "publicKey")));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(B64(key, "publicKey"))).ToLowerInvariant(), P256.Thumbprint(Str(key, "publicKey")));
    }

    [Fact]
    public void Keys_on_other_curves_or_compressed_are_refused()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.False(P256.IsPublicKey(Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo())));

        var spki = B64(Section("installProof").GetProperty("key"), "publicKey");
        Assert.False(P256.IsPublicKey(Convert.ToBase64String(spki[..^1])));
        Assert.False(P256.IsPublicKey("not base64"));
        Assert.False(P256.IsPublicKey(""));
    }

    // ---- CEK wrap ----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_cek_wrap_matches_the_fixture_byte_for_byte()
    {
        var w = Section("cekWrap");
        using var ephemeral = ECDiffieHellman.Create(PrivateKey(w.GetProperty("ephemeralKey")));

        var wrapped = CekWrap.Wrap(
            Str(w.GetProperty("agreementKey"), "publicKey"), B64(w, "releaseSecret"), Guid.Parse(Str(w, "clientInstallId")),
            Str(w, "configKey"), Str(w, "blobSha256"), B64(w, "cek"), ephemeral, B64(w, "nonce"));

        var expected = w.GetProperty("wrapped");
        Assert.Equal(Str(w, "info"), CekWrap.Info(Guid.Parse(Str(w, "clientInstallId")), Str(w, "configKey"), Str(w, "blobSha256")));
        Assert.Equal(B64(expected, "ephemeralKey"), wrapped.EphemeralKey);
        Assert.Equal(B64(expected, "nonce"), wrapped.Nonce);
        Assert.Equal(B64(expected, "ciphertext"), wrapped.Ciphertext);
        Assert.Equal(B64(expected, "tag"), wrapped.Tag);
    }

    [Fact]
    public void The_intermediate_values_are_the_fixtures()
    {
        var w = Section("cekWrap");
        using var ephemeral = ECDiffieHellman.Create(PrivateKey(w.GetProperty("ephemeralKey")));
        using var agreement = P256.ImportAgreementKey(Str(w.GetProperty("agreementKey"), "publicKey"));

        var shared = ephemeral.DeriveRawSecretAgreement(agreement.PublicKey);
        Assert.Equal(Str(w, "sharedSecret"), Convert.ToHexString(shared).ToLowerInvariant());
        var kek = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, B64(w, "releaseSecret"), Encoding.UTF8.GetBytes(Str(w, "info")));
        Assert.Equal(Str(w, "kek"), Convert.ToHexString(kek).ToLowerInvariant());
    }

    [Theory]
    [MemberData(nameof(Names), "cekWrap", "attempts")]
    public void Unwrapping(string name)
    {
        var w = Section("cekWrap");
        var a = Case("cekWrap", "attempts", name);
        var e = w.GetProperty("wrapped");
        var wrapped = new WrappedCek(B64(e, "ephemeralKey"), B64(e, "nonce"), B64(e, "ciphertext"), B64(e, "tag"));
        using var agreement = ECDiffieHellman.Create(PrivateKey(w.GetProperty("agreementKey")));

        byte[] Open() => CekWrap.Unwrap(agreement, wrapped, B64(a, "releaseSecret"), Guid.Parse(Str(a, "clientInstallId")),
            Str(a, "configKey"), Str(a, "blobSha256"));

        if (a.GetProperty("opens").GetBoolean())
            Assert.Equal(B64(w, "cek"), Open());
        else
            Assert.ThrowsAny<CryptographicException>(Open);
    }

    // ---- config blob -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_blob_decrypts_and_encrypts_as_the_fixture_says()
    {
        var b = Section("configBlob");
        var blob = B64(b, "blob");

        Assert.Equal(Str(b, "blobSha256"), Convert.ToHexString(SHA256.HashData(blob)).ToLowerInvariant());
        Assert.True(ConfigBlob.HasValidHeader(blob));
        Assert.Equal(Str(b, "plaintext"), Encoding.UTF8.GetString(ConfigBlob.Decrypt(blob, B64(b, "cek"))));
        Assert.Equal(blob, ConfigBlob.Encrypt(Encoding.UTF8.GetBytes(Str(b, "plaintext")), B64(b, "cek"), B64(b, "nonce")));
        Assert.ThrowsAny<CryptographicException>(() => ConfigBlob.Decrypt(blob, B64(b, "otherCek")));
    }

    [Theory]
    [MemberData(nameof(Names), "configBlob", "invalid")]
    public void A_broken_blob_is_refused(string name)
    {
        var b = Section("configBlob");
        var blob = B64(Case("configBlob", "invalid", name), "blob");
        Assert.ThrowsAny<CryptographicException>(() => ConfigBlob.Decrypt(blob, B64(b, "cek")));
    }

    // ---- offline lease -----------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Names), "offlineLease", "cases")]
    public void Offline_lease(string name)
    {
        var c = Case("offlineLease", "cases", name);
        var t = c.GetProperty("terms");
        var terms = new LeaseTerms(
            t.GetProperty("accountId").GetInt32(), Guid.Parse(Str(t, "clientInstallId")), Str(t, "serialNumber"),
            Str(t, "configKey"), Str(t, "blobSha256"),
            DateTimeOffset.Parse(Str(t, "issuedAt")), DateTimeOffset.Parse(Str(t, "notAfter")));

        Assert.Equal(Str(t, "issuedAt"), OfflineLease.FormatTime(terms.IssuedAt));
        Assert.Equal(Str(c, "statement"), OfflineLease.Statement(terms));
        Assert.Equal(c.GetProperty("valid").GetBoolean(), OfflineLease.Verify(Str(c, "publicKey"), Str(c, "statement"), Str(c, "signature")));
    }

    [Fact]
    public void A_lease_the_server_signs_verifies_against_its_public_key()
    {
        var key = Section("offlineLease").GetProperty("key");
        using var signer = ECDsa.Create(PrivateKey(key));
        var terms = new LeaseTerms(7, Guid.NewGuid(), "MSV3G01SATKX", "ms5", new string('a', 64),
            OfflineLease.ToSeconds(DateTimeOffset.UtcNow), OfflineLease.ToSeconds(DateTimeOffset.UtcNow.AddDays(7)));

        Assert.True(OfflineLease.Verify(Str(key, "publicKey"), OfflineLease.Statement(terms), OfflineLease.Sign(signer, terms)));
    }
}
