using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Intelimensa.Accounts.ConfigDelivery;

/// <summary>
/// The server's key-encryption key (KEK, <c>Configs:KeyEncryptionKey</c>): wraps the secrets the DB holds
/// (config CEKs, release secrets), so a leaked DB or backup on its own yields none of them. AES-256-GCM,
/// stored as base64 <c>nonce(12) ‖ tag(16) ‖ ciphertext</c>, with a purpose string as associated data
/// (<c>config-cek/ms5</c>, <c>release-secret/1.4.2/MacOSArm64</c>) so a wrapped value can't be moved to
/// another row. Also holds the lease-signing key, the other delivery secret.
/// </summary>
public sealed class KeyEncryption : IDisposable
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[]? _kek;
    private readonly ECDsa? _leaseKey;

    public KeyEncryption(IOptions<ConfigDeliveryOptions> options)
    {
        var o = options.Value;
        if (!string.IsNullOrWhiteSpace(o.KeyEncryptionKey))
        {
            _kek = Convert.FromBase64String(o.KeyEncryptionKey.Trim());
            if (_kek.Length != 32)
                throw new InvalidOperationException("Configs:KeyEncryptionKey must be 32 bytes, base64.");
        }

        if (!string.IsNullOrWhiteSpace(o.LeaseSigningKey))
        {
            _leaseKey = ECDsa.Create();
            _leaseKey.ImportPkcs8PrivateKey(Convert.FromBase64String(o.LeaseSigningKey.Trim()), out _);
            if (_leaseKey.KeySize != 256)
                throw new InvalidOperationException("Configs:LeaseSigningKey must be a P-256 key.");
            LeasePublicKey = Convert.ToBase64String(_leaseKey.ExportSubjectPublicKeyInfo());
        }

        KeyId = o.KeyEncryptionKeyId;
    }

    public string KeyId { get; }

    /// <summary>Both delivery secrets are configured.</summary>
    public bool IsAvailable => _kek is not null && _leaseKey is not null;

    public bool CanWrap => _kek is not null;

    /// <summary>The public half of the lease-signing key, base64 SPKI: what AxoSync embeds.</summary>
    public string? LeasePublicKey { get; }

    public ECDsa LeaseKey => _leaseKey ?? throw new InvalidOperationException("Configs:LeaseSigningKey is not configured.");

    public static string ConfigCekPurpose(string configKey) => $"config-cek/{configKey}";

    public static string ReleaseSecretPurpose(string version, string platform) => $"release-secret/{version}/{platform}";

    public string Wrap(string purpose, ReadOnlySpan<byte> secret)
    {
        var kek = _kek ?? throw new InvalidOperationException("Configs:KeyEncryptionKey is not configured.");
        var output = new byte[NonceSize + TagSize + secret.Length];
        RandomNumberGenerator.Fill(output.AsSpan(0, NonceSize));
        using var aes = new AesGcm(kek, TagSize);
        aes.Encrypt(output.AsSpan(0, NonceSize), secret, output.AsSpan(NonceSize + TagSize), output.AsSpan(NonceSize, TagSize),
            Encoding.UTF8.GetBytes(purpose));
        return Convert.ToBase64String(output);
    }

    /// <summary>Throws <see cref="CryptographicException"/> if the value wasn't wrapped by this KEK for this purpose.</summary>
    public byte[] Unwrap(string purpose, string wrapped)
    {
        var kek = _kek ?? throw new InvalidOperationException("Configs:KeyEncryptionKey is not configured.");
        var input = Convert.FromBase64String(wrapped);
        if (input.Length < NonceSize + TagSize) throw new CryptographicException("Wrapped value too short.");

        var secret = new byte[input.Length - NonceSize - TagSize];
        using var aes = new AesGcm(kek, TagSize);
        aes.Decrypt(input.AsSpan(0, NonceSize), input.AsSpan(NonceSize + TagSize), input.AsSpan(NonceSize, TagSize), secret,
            Encoding.UTF8.GetBytes(purpose));
        return secret;
    }

    public void Dispose() => _leaseKey?.Dispose();
}
