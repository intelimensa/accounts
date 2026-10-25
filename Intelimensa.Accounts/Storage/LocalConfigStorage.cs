using System.Security.Cryptography;
using Intelimensa.Accounts.ConfigDelivery;
using Microsoft.Extensions.Options;

namespace Intelimensa.Accounts.Storage;

/// <summary>
/// Stores blobs as <c>{root}/{sha256}.cfg</c>, root <c>Configs:StoragePath</c>. Same rules as release
/// storage: outside the web root (blobs go out only through the authenticated API) and outside the deploy
/// directory, or an rsync --delete deploy would wipe it. The blobs are encrypted; their keys are in the DB,
/// wrapped under the KEK, and never on this disk.
/// </summary>
public class LocalConfigStorage : IConfigStorage
{
    private readonly string _root;

    public LocalConfigStorage(IOptions<ConfigDeliveryOptions> options, IHostEnvironment env)
    {
        _root = Path.GetFullPath(options.Value.StoragePath, env.ContentRootPath);
    }

    public async Task<StoredConfigBlob> SaveAsync(byte[] blob, CancellationToken ct)
    {
        var sha256 = Convert.ToHexString(SHA256.HashData(blob)).ToLowerInvariant();
        var key = sha256 + ".cfg";
        var path = ResolvePath(key);
        Directory.CreateDirectory(_root);

        var temp = path + ".uploading";
        try
        {
            await File.WriteAllBytesAsync(temp, blob, ct);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            throw;
        }
        return new StoredConfigBlob(key, blob.Length, sha256);
    }

    public Stream OpenRead(string storageKey) =>
        new FileStream(ResolvePath(storageKey), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

    public void Delete(string storageKey)
    {
        var path = ResolvePath(storageKey);
        if (File.Exists(path)) File.Delete(path);
    }

    private string ResolvePath(string storageKey)
    {
        var full = Path.GetFullPath(storageKey, _root);
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Storage key escapes the storage root.");
        return full;
    }
}
