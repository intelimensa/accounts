using System.Security.Cryptography;
using System.Text;

namespace Intelimensa.Accounts.ConfigDelivery;

/// <summary>A config's content key as delivered to one client install, for one AxoSync build.</summary>
public record WrappedCek(byte[] EphemeralKey, byte[] Nonce, byte[] Ciphertext, byte[] Tag);

/// <summary>
/// Wraps a config's content key (CEK) for one client install and one AxoSync build, so that only that
/// install's private agreement key together with the secret compiled into that build can unwrap it:
/// <code>
/// shared  = ECDH-P256(fresh ephemeral key, install agreement key)   (the x coordinate, 32 bytes)
/// kek     = HKDF-SHA256(ikm = shared, salt = release secret, info = Info(...), 32 bytes)
/// wrapped = AES-256-GCM(kek, nonce, CEK), no associated data
/// </code>
/// A token replayed from a script yields a wrapped key it can't open without the install's private key;
/// a script with its own registered install still lacks the build's secret. Pinned by
/// <c>testdata/config-delivery-vectors.json</c>; AxoSync implements <see cref="Unwrap"/>.
/// </summary>
public static class CekWrap
{
    public const string Tag = "intelimensa-cek-wrap-v1";
    public const int ReleaseSecretSize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;

    /// <summary>The HKDF info: binds the key to the install, the config and the exact blob it opens.</summary>
    public static string Info(Guid clientInstallId, string configKey, string blobSha256) =>
        $"{Tag}\n{clientInstallId:D}\n{configKey}\n{blobSha256}\n";

    public static WrappedCek Wrap(
        string agreementKeySpki,
        ReadOnlySpan<byte> releaseSecret,
        Guid clientInstallId,
        string configKey,
        string blobSha256,
        ReadOnlySpan<byte> cek,
        ECDiffieHellman? ephemeral = null,
        byte[]? nonce = null)
    {
        if (releaseSecret.Length != ReleaseSecretSize) throw new ArgumentException("A release secret is 32 bytes.", nameof(releaseSecret));
        if (cek.Length != ConfigBlob.KeySize) throw new ArgumentException("A config key is 32 bytes.", nameof(cek));

        using var peer = P256.ImportAgreementKey(agreementKeySpki);
        var ownEphemeral = ephemeral is null;
        ephemeral ??= ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            var shared = ephemeral.DeriveRawSecretAgreement(peer.PublicKey);
            var kek = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, releaseSecret.ToArray(),
                Encoding.UTF8.GetBytes(Info(clientInstallId, configKey, blobSha256)));
            CryptographicOperations.ZeroMemory(shared);

            nonce ??= RandomNumberGenerator.GetBytes(NonceSize);
            var ciphertext = new byte[cek.Length];
            var tag = new byte[TagSize];
            using (var aes = new AesGcm(kek, TagSize))
                aes.Encrypt(nonce, cek, ciphertext, tag);
            CryptographicOperations.ZeroMemory(kek);

            return new WrappedCek(ephemeral.ExportSubjectPublicKeyInfo(), nonce, ciphertext, tag);
        }
        finally
        {
            if (ownEphemeral) ephemeral.Dispose();
        }
    }

    /// <summary>The client's side, here for tests. Throws <see cref="CryptographicException"/> when anything doesn't match.</summary>
    public static byte[] Unwrap(
        ECDiffieHellman agreementKey,
        WrappedCek wrapped,
        ReadOnlySpan<byte> releaseSecret,
        Guid clientInstallId,
        string configKey,
        string blobSha256)
    {
        using var ephemeral = P256.ImportAgreementKey(Convert.ToBase64String(wrapped.EphemeralKey));
        var shared = agreementKey.DeriveRawSecretAgreement(ephemeral.PublicKey);
        var kek = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, releaseSecret.ToArray(),
            Encoding.UTF8.GetBytes(Info(clientInstallId, configKey, blobSha256)));

        var cek = new byte[wrapped.Ciphertext.Length];
        using var aes = new AesGcm(kek, TagSize);
        aes.Decrypt(wrapped.Nonce, wrapped.Ciphertext, wrapped.Tag, cek);
        return cek;
    }
}
