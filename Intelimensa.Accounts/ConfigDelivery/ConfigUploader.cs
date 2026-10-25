using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Intelimensa.Accounts.Storage;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.ConfigDelivery;

/// <summary>
/// Staff upload ConfigGen's output for a config: <c>&lt;stem&gt;.cfg</c> (the v2 blob) and <c>&lt;stem&gt;.key</c> (its CEK,
/// base64). The pair is test-decrypted here, so a mismatched pair is refused at upload rather than on a
/// participant's machine; the plaintext only lives in memory for that check. The blob is stored under its
/// SHA-256 and the CEK wrapped under the KEK. Uploading to a config that has a file replaces it, which is how
/// a config's key is rotated (docs/config-management.md, "Rotation").
/// </summary>
public partial class ConfigUploader(
    ApplicationDbContext db,
    IConfigStorage storage,
    KeyEncryption keys,
    ILogger<ConfigUploader> logger)
{
    public const int MaxBlobBytes = 4 * 1024 * 1024;

    [GeneratedRegex(@"^\s*\[[^\]\r\n]+\]", RegexOptions.Multiline)]
    private static partial Regex IniSection();

    /// <summary>Returns null on success, or what is wrong.</summary>
    public async Task<string?> UploadAsync(int configId, byte[] blob, string keyText, string userId, CancellationToken ct)
    {
        if (!keys.CanWrap)
            return "Config delivery isn't set up on this server (Configs:KeyEncryptionKey is missing).";

        var config = await db.Configs.FirstOrDefaultAsync(c => c.Id == configId, ct);
        if (config is null) return "No such config.";
        if (blob.Length > MaxBlobBytes) return "The config file is larger than 4 MB.";
        if (!ConfigBlob.HasValidHeader(blob))
            return "That isn't a v2 config file (.cfg from ConfigGen's publish mode).";

        byte[] cek;
        try
        {
            cek = Convert.FromBase64String(keyText.Trim());
        }
        catch (FormatException)
        {
            return "The key file isn't base64.";
        }
        if (cek.Length != ConfigBlob.KeySize) return "The key must be 32 bytes.";

        byte[] plaintext;
        try
        {
            plaintext = ConfigBlob.Decrypt(blob, cek);
        }
        catch (CryptographicException)
        {
            return "The key doesn't open this config file. Upload the .cfg and .key ConfigGen wrote together.";
        }

        try
        {
            if (!LooksLikeIni(plaintext))
                return "The file decrypts, but the result doesn't look like a config (no INI sections).";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        var stored = await storage.SaveAsync(blob, ct);
        var previous = config.BlobStorageKey;

        config.BlobStorageKey = stored.StorageKey;
        config.BlobSha256 = stored.Sha256;
        config.BlobSize = stored.SizeBytes;
        config.WrappedCek = keys.Wrap(KeyEncryption.ConfigCekPurpose(config.Key), cek);
        config.KekId = keys.KeyId;
        config.UploadedAt = DateTimeOffset.UtcNow;
        config.UploadedByUserId = userId;
        config.UpdatedAt = DateTimeOffset.UtcNow;
        CryptographicOperations.ZeroMemory(cek);
        await db.SaveChangesAsync(ct);

        if (previous is not null && previous != stored.StorageKey &&
            !await db.Configs.AnyAsync(c => c.BlobStorageKey == previous, ct))
            storage.Delete(previous);

        logger.LogInformation("Config {Key}: uploaded blob {Sha256} ({Size} bytes) by user {UserId}",
            config.Key, stored.Sha256, stored.SizeBytes, userId);
        return null;
    }

    private static bool LooksLikeIni(byte[] plaintext)
    {
        try
        {
            var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(plaintext);
            return IniSection().IsMatch(text);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
