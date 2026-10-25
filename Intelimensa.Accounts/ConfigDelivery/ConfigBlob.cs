using System.Security.Cryptography;

namespace Intelimensa.Accounts.ConfigDelivery;

/// <summary>
/// The v2 encrypted-config format ConfigGen's publish mode writes and AxoSync decrypts:
/// <c>"IMCF" ‖ version(1) = 2 ‖ nonce(12) ‖ tag(16) ‖ ciphertext</c>, AES-256-GCM under the config's
/// random content key (CEK), with the 5-byte header as associated data. Unlike v1 there is no PBKDF2:
/// v1 stretched a human passphrase, and a random 256-bit key has nothing to stretch. Pinned by
/// <c>testdata/config-delivery-vectors.json</c> (<c>scripts/gen-config-delivery-vectors.py</c>).
/// The server never decrypts a blob except to check an upload (<see cref="ConfigUploader"/>).
/// </summary>
public static class ConfigBlob
{
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const byte Version = 2;

    private static readonly byte[] Magic = "IMCF"u8.ToArray();

    public static int HeaderSize => Magic.Length + 1;

    public static int MinimumSize => HeaderSize + NonceSize + TagSize;

    /// <summary>True when the bytes start like a v2 blob and are long enough to hold one.</summary>
    public static bool HasValidHeader(ReadOnlySpan<byte> blob) =>
        blob.Length >= MinimumSize && blob[..Magic.Length].SequenceEqual(Magic) && blob[Magic.Length] == Version;

    /// <summary>Decrypts a blob; throws <see cref="CryptographicException"/> on a wrong key, a bad header or any tampering.</summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> cek)
    {
        if (cek.Length != KeySize) throw new CryptographicException("A config key is 32 bytes.");
        if (!HasValidHeader(blob)) throw new CryptographicException("Not a v2 config blob.");

        var header = blob[..HeaderSize];
        var nonce = blob.Slice(HeaderSize, NonceSize);
        var tag = blob.Slice(HeaderSize + NonceSize, TagSize);
        var ciphertext = blob[MinimumSize..];

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(cek, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, header);
        return plaintext;
    }

    /// <summary>Encrypts a config. Production blobs come from ConfigGen; this exists for tests and fixtures.</summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> cek, ReadOnlySpan<byte> nonce)
    {
        if (cek.Length != KeySize) throw new ArgumentException("A config key is 32 bytes.", nameof(cek));
        if (nonce.Length != NonceSize) throw new ArgumentException("The nonce is 12 bytes.", nameof(nonce));

        var blob = new byte[MinimumSize + plaintext.Length];
        Magic.CopyTo(blob, 0);
        blob[Magic.Length] = Version;
        nonce.CopyTo(blob.AsSpan(HeaderSize));

        using var aes = new AesGcm(cek, TagSize);
        aes.Encrypt(nonce, plaintext, blob.AsSpan(MinimumSize), blob.AsSpan(HeaderSize + NonceSize, TagSize), blob.AsSpan(0, HeaderSize));
        return blob;
    }
}
