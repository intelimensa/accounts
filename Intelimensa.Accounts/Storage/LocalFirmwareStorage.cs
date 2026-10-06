using System.Security.Cryptography;
using Intelimensa.Accounts.Models;
using Microsoft.Extensions.Options;

namespace Intelimensa.Accounts.Storage;

public class FirmwareStorageOptions
{
    /// <summary>
    /// Root directory for firmware files. Relative paths resolve against the content root. Not
    /// "firmware": on a case-insensitive filesystem that is the <c>Firmware/</c> source folder.
    /// </summary>
    public string StoragePath { get; set; } = "firmware-files";
}

/// <summary>
/// Stores builds as <c>{root}/{deviceType}/{kind}/{version}/{fileName}</c>. Like release storage, the
/// root must live outside the web root and outside the rsync-deployed app directory (see
/// docs/deployment-vps.md); files are only ever served through the manufacturing API.
/// </summary>
public class LocalFirmwareStorage : IFirmwareStorage
{
    private readonly string _root;

    public LocalFirmwareStorage(IOptions<FirmwareStorageOptions> options, IHostEnvironment env)
    {
        _root = Path.GetFullPath(options.Value.StoragePath, env.ContentRootPath);
    }

    public async Task<StoredArtifact> SaveAsync(
        string deviceType, FirmwareKind kind, string version, string fileName, Stream content, CancellationToken ct)
    {
        var key = $"{deviceType}/{kind}/{version}/{fileName}";
        var finalPath = ResolvePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        // Temp file first, so a failed upload never leaves a partial image at the real key.
        var tempPath = finalPath + ".uploading";
        try
        {
            using var sha = SHA256.Create();
            long size = 0;
            await using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    size += read;
                }
                sha.TransformFinalBlock([], 0, 0);
            }

            File.Move(tempPath, finalPath, overwrite: true);
            return new StoredArtifact(key, size, Convert.ToHexString(sha.Hash!).ToLowerInvariant());
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
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
