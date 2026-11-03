using Intelimensa.Accounts.ServerInfo;
using Xunit;

namespace Intelimensa.Accounts.Tests;

public class ServerUsageTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(999, "999 B")]
    [InlineData(1500, "1.5 kB")]
    [InlineData(300_000_000_000, "300 GB")]
    public void ByteSize_uses_powers_of_1000(long bytes, string expected) =>
        Assert.Equal(expected, ByteSize.Format(bytes));

    [Fact]
    public void TreeBytes_sums_nested_files_and_tolerates_missing_paths()
    {
        var dir = Directory.CreateTempSubdirectory("usage-test").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "a", "b"));
            File.WriteAllBytes(Path.Combine(dir, "x"), new byte[10]);
            File.WriteAllBytes(Path.Combine(dir, "a", "b", "y"), new byte[32]);
            Assert.Equal(42, ServerUsageService.TreeBytes(dir));
            Assert.Equal(10, ServerUsageService.TreeBytes(Path.Combine(dir, "x")));
            Assert.Equal(0, ServerUsageService.TreeBytes(Path.Combine(dir, "missing")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Other_is_used_space_minus_items_on_the_same_volume_only()
    {
        var disk = new DiskInfo("/", 1000, 600, 350);
        var items = new[]
        {
            new StorageItem("db", "", "/d", 100, true, true),
            new StorageItem("releases", "", "/r", 200, true, true),
            new StorageItem("elsewhere", "", "/mnt/x", 500, true, false),
        };
        Assert.Equal(300, ServerUsageService.Summarize(disk, items).OtherBytes);
        Assert.Equal(0, ServerUsageService.Summarize(new DiskInfo("/", 1000, 100, 900), items).OtherBytes);
        Assert.Equal(0, ServerUsageService.Summarize(null, items).OtherBytes);
    }
}
