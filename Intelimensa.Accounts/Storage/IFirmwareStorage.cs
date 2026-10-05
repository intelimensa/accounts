using Intelimensa.Accounts.Models;

namespace Intelimensa.Accounts.Storage;

/// <summary>Where firmware hex files live. Same shape as <see cref="IReleaseStorage"/>.</summary>
public interface IFirmwareStorage
{
    Task<StoredArtifact> SaveAsync(
        string deviceType, FirmwareKind kind, string version, string fileName, Stream content, CancellationToken ct);

    Stream OpenRead(string storageKey);

    void Delete(string storageKey);
}
