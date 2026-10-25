using System.Security.Cryptography;

namespace Intelimensa.Accounts.ConfigDelivery;

/// <summary>
/// Reading the P-256 public keys clients send: base64 SubjectPublicKeyInfo (DER) with an uncompressed point,
/// the same encoding the release-signing keys use (91 bytes). Anything else is refused, so a client can't
/// pick another curve. The check is on the bytes rather than on what the platform reports about the curve,
/// which differs between operating systems.
/// </summary>
public static class P256
{
    // SEQUENCE { SEQUENCE { id-ecPublicKey, prime256v1 }, BIT STRING { 0x04 ... } }
    private static readonly byte[] SpkiPrefix = Convert.FromHexString("3059301306072a8648ce3d020106082a8648ce3d03010703420004");

    public const int SpkiSize = 91;

    public static ECDsa ImportSigningKey(string spkiBase64)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(Spki(spkiBase64), out _);
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    public static ECDiffieHellman ImportAgreementKey(string spkiBase64)
    {
        var key = ECDiffieHellman.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(Spki(spkiBase64), out _);
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>True when the text is a base64 P-256 public key.</summary>
    public static bool IsPublicKey(string? spkiBase64)
    {
        if (string.IsNullOrWhiteSpace(spkiBase64)) return false;
        try
        {
            using var key = ImportSigningKey(spkiBase64);
            return true;
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return false;
        }
    }

    /// <summary>Lowercase hex SHA-256 of the DER key: how an install's signing key is looked up.</summary>
    public static string Thumbprint(string spkiBase64) =>
        Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(spkiBase64))).ToLowerInvariant();

    private static byte[] Spki(string spkiBase64)
    {
        var der = Convert.FromBase64String(spkiBase64);
        if (der.Length != SpkiSize || !der.AsSpan(0, SpkiPrefix.Length).SequenceEqual(SpkiPrefix))
            throw new CryptographicException("Only uncompressed P-256 public keys are accepted.");
        return der;
    }
}
