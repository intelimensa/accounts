using Intelimensa.Accounts.ConfigDelivery;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Intelimensa.Accounts.Publishing;
using Intelimensa.Accounts.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Intelimensa.Accounts.Tests.Publishing;

public class ReleasePurgeTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ApplicationDbContext _db;
    private readonly FakeStorage _storage = new();
    private readonly ReleasePublisher _publisher;

    public ReleasePurgeTests()
    {
        _connection.Open();
        _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _publisher = new ReleasePublisher(
            _db, _storage, Options.Create(new ReleasePublishingOptions()),
            new KeyEncryption(Options.Create(new ConfigDeliveryOptions())), NullLogger<ReleasePublisher>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<Release> AddReleaseAsync(string version, ReleaseStatus status, params long[] sizes)
    {
        var release = new Release { Version = version, Status = status, PublishedAt = DateTimeOffset.UtcNow };
        var platforms = Enum.GetValues<ReleasePlatform>();
        for (var i = 0; i < sizes.Length; i++)
        {
            var key = $"{version}/{platforms[i]}/app.bin";
            _storage.Files.Add(key);
            release.Artifacts.Add(new ReleaseArtifact
            {
                Platform = platforms[i], FileName = "app.bin", SizeBytes = sizes[i], Sha256 = "00", StorageKey = key,
            });
        }
        _db.Releases.Add(release);
        await _db.SaveChangesAsync();
        return release;
    }

    [Fact]
    public async Task Purging_a_withdrawn_release_deletes_files_and_rows_and_keeps_the_release()
    {
        var release = await AddReleaseAsync("1.0.0", ReleaseStatus.Withdrawn, 100, 250);
        var other = await AddReleaseAsync("1.1.0", ReleaseStatus.Published, 40);

        var result = await _publisher.PurgeFilesAsync(release.Id);

        Assert.True(result.Ok);
        Assert.Equal(350, result.Value);
        var kept = await _db.Releases.Include(r => r.Artifacts).SingleAsync(r => r.Id == release.Id);
        Assert.Empty(kept.Artifacts);
        Assert.NotNull(kept.FilesPurgedAt);
        Assert.Equal(ReleaseStatus.Withdrawn, kept.Status);
        Assert.Equal(["1.1.0/WindowsX64/app.bin"], _storage.Files);
        Assert.Single(await _db.ReleaseArtifacts.Where(a => a.ReleaseId == other.Id).ToListAsync());
    }

    [Theory]
    [InlineData(ReleaseStatus.Published)]
    [InlineData(ReleaseStatus.Draft)]
    public async Task Only_withdrawn_releases_can_be_purged(ReleaseStatus status)
    {
        var release = await AddReleaseAsync("2.0.0", status, 10);

        var result = await _publisher.PurgeFilesAsync(release.Id);

        Assert.Equal(PublishError.Conflict, result.Error);
        Assert.Single(_storage.Files);
        Assert.Single(await _db.ReleaseArtifacts.ToListAsync());
    }

    [Fact]
    public async Task Purging_twice_or_an_unknown_release_is_refused()
    {
        var release = await AddReleaseAsync("3.0.0", ReleaseStatus.Withdrawn, 10);
        Assert.True((await _publisher.PurgeFilesAsync(release.Id)).Ok);

        Assert.Equal(PublishError.Conflict, (await _publisher.PurgeFilesAsync(release.Id)).Error);
        Assert.Equal(PublishError.NotFound, (await _publisher.PurgeFilesAsync(9999)).Error);
    }

    [Fact]
    public async Task Bulk_purge_skips_what_cannot_be_purged_and_still_purges_the_rest()
    {
        var a = await AddReleaseAsync("4.0.0", ReleaseStatus.Withdrawn, 100);
        var b = await AddReleaseAsync("4.1.0", ReleaseStatus.Withdrawn, 200);
        var live = await AddReleaseAsync("4.2.0", ReleaseStatus.Published, 50);

        var summary = await _publisher.PurgeFilesAsync(new[] { a.Id, live.Id, b.Id, a.Id });

        Assert.Equal(["4.0.0", "4.1.0"], summary.Purged);
        Assert.Equal(300, summary.BytesFreed);
        Assert.Single(summary.Skipped);
        Assert.StartsWith("4.2.0:", summary.Skipped[0]);
        Assert.Equal(["4.2.0/WindowsX64/app.bin"], _storage.Files);
    }

    private sealed class FakeStorage : IReleaseStorage
    {
        public List<string> Files { get; } = [];

        public Task<StoredArtifact> SaveAsync(string version, ReleasePlatform platform, string fileName, Stream content, CancellationToken ct) =>
            throw new NotSupportedException();

        public Stream OpenRead(string storageKey) => throw new NotSupportedException();

        public void Delete(string storageKey) => Files.Remove(storageKey);
    }
}
