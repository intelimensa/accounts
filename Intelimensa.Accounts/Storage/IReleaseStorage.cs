using Intelimensa.Accounts.Models;

namespace Intelimensa.Accounts.Storage;

/// <summary>
/// Where release binaries live. Local disk today; the interface exists so moving to object
/// storage later doesn't touch the pages or the data model.
/// </summary>
public interface IReleaseStorage
{
    Task<StoredArtifact> SaveAsync(
        string version, ReleasePlatform platform, string fileName, Stream content, CancellationToken ct);

    Stream OpenRead(string storageKey);

    void Delete(string storageKey);
}

public record StoredArtifact(string StorageKey, long SizeBytes, string Sha256);
