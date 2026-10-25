namespace Intelimensa.Accounts.Storage;

/// <summary>
/// Where encrypted config blobs live. Local disk today, like <see cref="IReleaseStorage"/>. Blobs are
/// stored under their SHA-256, so replacing a config's file never overwrites a blob a client may still be
/// downloading; the old one is deleted once the row points elsewhere.
/// </summary>
public interface IConfigStorage
{
    Task<StoredConfigBlob> SaveAsync(byte[] blob, CancellationToken ct);

    Stream OpenRead(string storageKey);

    void Delete(string storageKey);
}

public record StoredConfigBlob(string StorageKey, long SizeBytes, string Sha256);
