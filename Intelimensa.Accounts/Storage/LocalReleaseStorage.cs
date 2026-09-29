using System.Security.Cryptography;
using Intelimensa.Accounts.Models;
using Microsoft.Extensions.Options;

namespace Intelimensa.Accounts.Storage;

public class ReleaseStorageOptions
{
    /// <summary>Root directory for release files. Relative paths resolve against the content root.</summary>
    public string StoragePath { get; set; } = "releases";
}

/// <summary>
/// Stores artifacts as <c>{root}/{version}/{platform}/{fileName}</c>. The root must live outside
/// the web root (nothing here is served as a static file -- downloads go through the
/// authenticated Download page) and outside the deploy directory, or an rsync --delete deploy
/// would wipe it.
/// </summary>
public class LocalReleaseStorage : IReleaseStorage
{
    private readonly string _root;

    public LocalReleaseStorage(IOptions<ReleaseStorageOptions> options, IHostEnvironment env)
    {
        _root = Path.GetFullPath(options.Value.StoragePath, env.ContentRootPath);
    }

    public async Task<StoredArtifact> SaveAsync(
        string version, ReleasePlatform platform, string fileName, Stream content, CancellationToken ct)
    {
        var key = $"{version}/{platform}/{fileName}";
        var finalPath = ResolvePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        // Write to a temp file first so a failed/aborted upload never leaves a partial artifact
        // at the real key.
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

    // Defense in depth: keys are built from validated inputs, but never trust that a key stays
    // inside the root.
    private string ResolvePath(string storageKey)
    {
        var full = Path.GetFullPath(storageKey, _root);
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Storage key escapes the storage root.");
        return full;
    }
}
