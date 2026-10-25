using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Intelimensa.Accounts.Publishing;
using Intelimensa.Accounts.Storage;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.ConfigDelivery;

/// <summary>
/// The config-publishing rules, shared by the staff page (<c>/Staff/Configs</c>) and the publisher API (ConfigGen's
/// <c>publish</c> command), the way <see cref="ReleasePublisher"/> serves builds. The roles match the releases': a
/// Publisher (or Staff) uploads a config's file, which waits as <i>pending</i>; Staff publish it, or a Publisher does
/// with ConfigGen's <c>--auto-publish</c>. Only a published file is delivered.
/// <para>An upload is ConfigGen's v2 blob plus its random content key (CEK). The pair is test-decrypted here, so a
/// mismatched pair is refused at upload rather than on a participant's machine; the plaintext lives only in memory for
/// that check. The blob is stored under its SHA-256 and the CEK wrapped under the KEK. Publishing a new file rotates
/// the config's key (docs/config-management.md, "Rotation").</para>
/// </summary>
public partial class ConfigPublisher(
    ApplicationDbContext db,
    IConfigStorage storage,
    KeyEncryption keys,
    ILogger<ConfigPublisher> logger)
{
    public const int MaxBlobBytes = 4 * 1024 * 1024;

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,49}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"^\s*\[[^\]\r\n]+\]", RegexOptions.Multiline)]
    private static partial Regex IniSection();

    /// <summary>Creates the config row, or renames an existing one; idempotent, so ConfigGen can run it every time.</summary>
    public async Task<PublishResult<Config>> CreateOrUpdateAsync(string key, string? displayName)
    {
        key = key.Trim();
        if (!KeyPattern().IsMatch(key))
            return Fail<Config>(PublishError.Invalid, "A config key is lower-case letters, digits, '.', '_' or '-', like ms5.");
        displayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        if (displayName is { Length: > 200 })
            return Fail<Config>(PublishError.Invalid, "The display name is limited to 200 characters.");

        var config = await db.Configs.FirstOrDefaultAsync(c => c.Key == key);
        if (config is null)
        {
            config = new Config { Key = key, DisplayName = displayName ?? key };
            db.Configs.Add(config);
            logger.LogInformation("Config {Key} created", key);
        }
        else if (displayName is not null && displayName != config.DisplayName)
        {
            config.DisplayName = displayName;
            config.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync();
        return Ok(config);
    }

    /// <summary>Stores a new file for a config as pending, replacing any earlier pending file.</summary>
    public async Task<PublishResult<Config>> UploadAsync(string key, byte[] blob, string? cekBase64, string userId, CancellationToken ct)
    {
        if (!keys.CanWrap)
            return Fail<Config>(PublishError.Invalid, "Config delivery isn't set up on this server (Configs:KeyEncryptionKey is missing).");

        var config = await db.Configs.FirstOrDefaultAsync(c => c.Key == key, ct);
        if (config is null) return Fail<Config>(PublishError.NotFound, $"No config {key}.");
        if (blob.Length > MaxBlobBytes) return Fail<Config>(PublishError.Invalid, "The config file is larger than 4 MB.");
        if (!ConfigBlob.HasValidHeader(blob))
            return Fail<Config>(PublishError.Invalid, "That isn't a v2 config file (ConfigGen's publish command writes them).");

        byte[] cek;
        try
        {
            cek = Convert.FromBase64String((cekBase64 ?? "").Trim());
        }
        catch (FormatException)
        {
            return Fail<Config>(PublishError.Invalid, "The key isn't base64.");
        }
        if (cek.Length != ConfigBlob.KeySize) return Fail<Config>(PublishError.Invalid, "The key must be 32 bytes.");

        try
        {
            var problem = Check(blob, cek);
            if (problem is not null) return Fail<Config>(PublishError.Invalid, problem);

            var stored = await storage.SaveAsync(blob, ct);
            var previousPending = config.PendingBlobStorageKey;

            config.PendingBlobStorageKey = stored.StorageKey;
            config.PendingBlobSha256 = stored.Sha256;
            config.PendingBlobSize = stored.SizeBytes;
            config.PendingWrappedCek = keys.Wrap(KeyEncryption.ConfigCekPurpose(config.Key), cek);
            config.PendingKekId = keys.KeyId;
            config.PendingUploadedAt = DateTimeOffset.UtcNow;
            config.PendingUploadedByUserId = userId;
            config.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            if (previousPending is not null && previousPending != stored.StorageKey) await DeleteIfUnusedAsync(previousPending);
            logger.LogInformation("Config {Key}: pending file {Sha256} ({Size} bytes) uploaded by user {UserId}",
                config.Key, stored.Sha256, stored.SizeBytes, userId);
            return Ok(config);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cek);
        }
    }

    /// <summary>Makes the pending file the live one: from then on it is what participants get, with its own key.</summary>
    public async Task<PublishResult<Config>> PublishAsync(string key, string userId)
    {
        var config = await db.Configs.FirstOrDefaultAsync(c => c.Key == key);
        if (config is null) return Fail<Config>(PublishError.NotFound, $"No config {key}.");
        if (!config.HasPendingFile) return Fail<Config>(PublishError.Conflict, $"Config {key} has no pending file to publish.");

        var previous = config.BlobStorageKey;
        config.BlobStorageKey = config.PendingBlobStorageKey;
        config.BlobSha256 = config.PendingBlobSha256;
        config.BlobSize = config.PendingBlobSize;
        config.WrappedCek = config.PendingWrappedCek;
        config.KekId = config.PendingKekId;
        config.UploadedAt = config.PendingUploadedAt;
        config.UploadedByUserId = config.PendingUploadedByUserId;
        config.PublishedAt = DateTimeOffset.UtcNow;
        config.PublishedByUserId = userId;
        ClearPending(config);
        config.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        if (previous is not null && previous != config.BlobStorageKey) await DeleteIfUnusedAsync(previous);
        logger.LogInformation("Config {Key}: file {Sha256} published by user {UserId}", config.Key, config.BlobSha256, userId);
        return Ok(config);
    }

    public async Task<PublishResult<Config>> DiscardPendingAsync(string key)
    {
        var config = await db.Configs.FirstOrDefaultAsync(c => c.Key == key);
        if (config is null) return Fail<Config>(PublishError.NotFound, $"No config {key}.");
        if (!config.HasPendingFile) return Fail<Config>(PublishError.Conflict, $"Config {key} has no pending file.");

        var pending = config.PendingBlobStorageKey!;
        ClearPending(config);
        config.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await DeleteIfUnusedAsync(pending);
        logger.LogInformation("Config {Key}: pending file discarded", config.Key);
        return Ok(config);
    }

    private static string? Check(byte[] blob, byte[] cek)
    {
        byte[] plaintext;
        try
        {
            plaintext = ConfigBlob.Decrypt(blob, cek);
        }
        catch (CryptographicException)
        {
            return "The key doesn't open this config file: upload the file and the key ConfigGen made together.";
        }

        try
        {
            var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(plaintext);
            return IniSection().IsMatch(text) ? null : "The file decrypts, but the result doesn't look like a config (no INI sections).";
        }
        catch (DecoderFallbackException)
        {
            return "The file decrypts, but the result isn't text.";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static void ClearPending(Config config)
    {
        config.PendingBlobStorageKey = null;
        config.PendingBlobSha256 = null;
        config.PendingBlobSize = null;
        config.PendingWrappedCek = null;
        config.PendingKekId = null;
        config.PendingUploadedAt = null;
        config.PendingUploadedByUserId = null;
    }

    // Blobs are stored by content, so one file may back a live and a pending slot, or two configs.
    private async Task DeleteIfUnusedAsync(string storageKey)
    {
        if (!await db.Configs.AnyAsync(c => c.BlobStorageKey == storageKey || c.PendingBlobStorageKey == storageKey))
            storage.Delete(storageKey);
    }

    private static PublishResult<T> Ok<T>(T value) => new(value, null, null);

    private static PublishResult<T> Fail<T>(PublishError error, string message) => new(default, error, message);
}
