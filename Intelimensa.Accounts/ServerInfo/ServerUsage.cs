using System.Reflection;
using System.Runtime.InteropServices;
using Intelimensa.Accounts.ConfigDelivery;
using Intelimensa.Accounts.Publishing;
using Intelimensa.Accounts.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Intelimensa.Accounts.ServerInfo;

/// <summary>One thing that takes space on the server's data volume.</summary>
/// <param name="OnDisk">False when its path is on another volume than the data volume, so it isn't part of that disk's usage.</param>
public record StorageItem(string Label, string Description, string Path, long Bytes, bool Exists, bool OnDisk);

public record DiskInfo(string MountPoint, long TotalBytes, long UsedBytes, long AvailableBytes);

/// <summary>The data volume, what the app knows is on it, and the rest (OS, packages, logs, everything unlisted).</summary>
public record ServerUsage(DiskInfo? Disk, IReadOnlyList<StorageItem> Items, long OtherBytes);

/// <summary>
/// Measures the server's disk and how much of it each part of the app takes. The OS and anything the app
/// doesn't know about can't be measured without root, so it is the remainder: used space minus the listed items.
/// </summary>
public class ServerUsageService(
    IHostEnvironment env,
    IConfiguration config,
    IOptions<ReleaseStorageOptions> releases,
    IOptions<FirmwareStorageOptions> firmware,
    IOptions<ConfigDeliveryOptions> configs,
    IOptions<ReleasePublishingOptions> publishing)
{
    public ServerUsage Measure()
    {
        var dbPath = DatabasePath();
        var dbDir = Path.GetDirectoryName(dbPath) ?? env.ContentRootPath;
        var appDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var backups = config["ServerInfo:BackupsPath"] is { Length: > 0 } custom
            ? Path.GetFullPath(custom, env.ContentRootPath)
            : Path.GetFullPath(Path.Combine(dbDir, "..", "backups"));

        var disk = DriveFor(dbDir);
        var items = new List<StorageItem>
        {
            Item("Active database", "The live SQLite file with its write-ahead log.", dbPath, disk, DatabaseBytes(dbPath)),
            Item("Database backups", "Nightly and pre-deploy backups (never pruned automatically).", backups, disk),
            Item("AxoSync release builds", "Installers uploaded through publishing.", Full(releases.Value.StoragePath), disk),
            Item("Firmware releases", "The firmware catalog Quarry flashes from.", Full(firmware.Value.StoragePath), disk),
            Item("Config files", "Encrypted BCI config blobs.", Full(configs.Value.StoragePath), disk),
            Item("Server app", "The running build.", appDir, disk),
        };
        var previous = Path.Combine(Path.GetDirectoryName(appDir) ?? appDir, Path.GetFileName(appDir) + ".prev");
        if (Directory.Exists(previous))
            items.Add(Item("Previous app build", "Kept for rollback.", previous, disk));

        return Summarize(disk, items);
    }

    /// <summary>Used space not accounted for by the items that live on the same volume.</summary>
    public static ServerUsage Summarize(DiskInfo? disk, IReadOnlyList<StorageItem> items)
    {
        var known = items.Where(i => i.OnDisk).Sum(i => i.Bytes);
        return new ServerUsage(disk, items, disk is null ? 0 : Math.Max(0, disk.UsedBytes - known));
    }

    public IReadOnlyList<(string Label, string Value)> Describe()
    {
        var info = GC.GetGCMemoryInfo();
        var uptime = DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime;
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var signingKeys = (publishing.Value.SigningPublicKeys?.Length ?? 0) + (string.IsNullOrWhiteSpace(publishing.Value.SigningPublicKey) ? 0 : 1);
        var delivery = !string.IsNullOrWhiteSpace(configs.Value.KeyEncryptionKey) && !string.IsNullOrWhiteSpace(configs.Value.LeaseSigningKey);

        return
        [
            ("Host", Environment.MachineName),
            ("Operating system", RuntimeInformation.OSDescription),
            ("Architecture", RuntimeInformation.OSArchitecture.ToString()),
            ("CPU cores", Environment.ProcessorCount.ToString()),
            ("Memory", ByteSize.Format(info.TotalAvailableMemoryBytes) + " available to the app, " + ByteSize.Format(Environment.WorkingSet) + " in use by it"),
            ("Runtime", RuntimeInformation.FrameworkDescription),
            ("App version", version),
            ("Environment", env.EnvironmentName),
            ("Public URL", config["App:PublicBaseUrl"] is { Length: > 0 } url ? url : "(not set)"),
            ("Running for", $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m"),
            ("Config delivery", delivery ? "on" : "off (keys not set)"),
            ("Release signing keys", signingKeys == 0 ? "none (builds are not verified)" : signingKeys.ToString()),
            ("Outgoing mail", config["Email:Host"] is { Length: > 0 } ? "SMTP" : "log only"),
        ];
    }

    private string Full(string path) => Path.GetFullPath(path, env.ContentRootPath);

    private string DatabasePath()
    {
        var source = new SqliteConnectionStringBuilder(config.GetConnectionString("DefaultConnection")).DataSource;
        return string.IsNullOrEmpty(source) ? Path.Combine(env.ContentRootPath, "accounts.db") : Path.GetFullPath(source);
    }

    /// <summary>The database file plus its -wal, -shm and -journal companions.</summary>
    private static long DatabaseBytes(string dbPath) =>
        new[] { "", "-wal", "-shm", "-journal" }.Sum(suffix => File.Exists(dbPath + suffix) ? new FileInfo(dbPath + suffix).Length : 0);

    private static StorageItem Item(string label, string description, string path, DiskInfo? disk, long? bytes = null)
    {
        var exists = Directory.Exists(path) || File.Exists(path);
        var size = bytes ?? (exists ? TreeBytes(path) : 0);
        var drive = DriveFor(path);
        return new StorageItem(label, description, path, size, exists, disk is not null && drive?.MountPoint == disk.MountPoint);
    }

    /// <summary>Total size of every file under a directory (or the file itself). Unreadable entries and symlinks are skipped.</summary>
    public static long TreeBytes(string path)
    {
        if (File.Exists(path)) return new FileInfo(path).Length;
        if (!Directory.Exists(path)) return 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        return new DirectoryInfo(path).EnumerateFiles("*", options).Sum(f => f.Length);
    }

    /// <summary>The volume holding a path: the mounted drive with the longest matching mount point.</summary>
    private static DiskInfo? DriveFor(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var drive = DriveInfo.GetDrives()
                .Where(d => d.IsReady && full.StartsWith(d.RootDirectory.FullName, StringComparison.Ordinal))
                .OrderByDescending(d => d.RootDirectory.FullName.Length)
                .FirstOrDefault();
            return drive is null ? null : new DiskInfo(drive.RootDirectory.FullName, drive.TotalSize, drive.TotalSize - drive.TotalFreeSpace, drive.AvailableFreeSpace);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

public static class ByteSize
{
    /// <summary>Human-readable size in powers of 1000, as disk vendors and <c>df -H</c> count (a "300 GB" plan shows as 300 GB).</summary>
    public static string Format(long bytes)
    {
        string[] units = ["B", "kB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1) { value /= 1000; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }
}
