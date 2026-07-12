using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Audio;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Download;

public class AutomaticUpgradeSweepTests
{
    private static readonly Guid Owner = WellKnownUsers.OwnerId;

    [Fact]
    public async Task Sweep_QueuesOnlyEligibleLossyBuiltSongs()
    {
        await using var db = CreateDbContext();
        var wanted = Song(1, ".opus");                                   // lossy + Done → eligible
        db.Songs.AddRange(
            wanted,
            Song(2, ".flac"),                                            // already lossless
            Song(3, ".mp3", build: LibraryBuildStatus.Pending),         // not built yet
            Song(4, ".m4a", synthetic: true),                           // synthetic
            Song(5, ".ogg", duplicate: true),                           // duplicate
            Song(6, ".mp3", owner: WellKnownUsers.DemoId),              // demo tenant
            Song(7, ".mp3", artist: ""));                               // missing identity
        await db.SaveChangesAsync();

        var channel = new QualityUpgradeChannel();
        var queued = await CreateSweep(db, channel).SweepAsync(default);

        Assert.Equal(1, queued);
        var request = await db.UpgradeRequests.SingleAsync();
        Assert.Equal(wanted.Id, request.SongId);
        Assert.Equal(UpgradeTrigger.Auto, request.Trigger);
        Assert.True(channel.Reader.TryRead(out var enqueuedId));
        Assert.Equal(request.Id, enqueuedId);
    }

    [Fact]
    public async Task Sweep_RespectsActiveRequestAndCooldown()
    {
        await using var db = CreateDbContext();
        db.Songs.AddRange(Song(1, ".opus"), Song(2, ".opus"), Song(3, ".opus"));
        // #1 has an in-flight request; #2 failed recently (inside cooldown); #3 failed long ago.
        db.UpgradeRequests.AddRange(
            Request(1, songId: 1, UpgradeRequestStatus.Searching, updatedDaysAgo: 0),
            Request(2, songId: 2, UpgradeRequestStatus.NotFound, updatedDaysAgo: 5),
            Request(3, songId: 3, UpgradeRequestStatus.NotFound, updatedDaysAgo: 45));
        await db.SaveChangesAsync();

        var queued = await CreateSweep(db, new QualityUpgradeChannel()).SweepAsync(default);

        Assert.Equal(1, queued); // only #3 (cooldown 30d elapsed) becomes eligible again
        // The freshly-queued request is the only one in Queued state (the seeded priors are terminal/active).
        var newRequest = await db.UpgradeRequests
            .Where(r => r.Status == UpgradeRequestStatus.Queued).SingleAsync();
        Assert.Equal(3, newRequest.SongId);
        Assert.Equal(UpgradeTrigger.Auto, newRequest.Trigger);
    }

    [Fact]
    public async Task Sweep_NoOpWhenDisabled()
    {
        await using var db = CreateDbContext();
        db.Songs.Add(Song(1, ".opus"));
        await db.SaveChangesAsync();

        var queued = await CreateSweep(db, new QualityUpgradeChannel(), enabled: false).SweepAsync(default);

        Assert.Equal(0, queued);
        Assert.Empty(await db.UpgradeRequests.ToListAsync());
    }

    [Fact]
    public async Task Sweep_NoOpWhenNoUpgradeProviderConfigured()
    {
        await using var db = CreateDbContext();
        db.Songs.Add(Song(1, ".opus"));
        await db.SaveChangesAsync();

        var queued = await CreateSweep(db, new QualityUpgradeChannel(), providerConfigured: false).SweepAsync(default);

        Assert.Equal(0, queued);
        Assert.Empty(await db.UpgradeRequests.ToListAsync());
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static AutomaticUpgradeSweep CreateSweep(
        MusicHoarderDbContext db, QualityUpgradeChannel channel,
        bool enabled = true, bool providerConfigured = true)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/src",
            DestinationDirectory = "/dest",
            DownloadProviders = ["fake"],
            EnableAutomaticQualityUpgrades = enabled,
            QualityUpgradeCooldownDays = 30,
            QualityUpgradeBatchSize = 50,
        });
        var providers = new IUpgradeProvider[] { new FakeUpgradeProvider("fake", providerConfigured) };
        return new AutomaticUpgradeSweep(
            db, providers, channel, new OwnerLookupService(), options,
            NullLogger<AutomaticUpgradeSweep>.Instance);
    }

    private static SongMetadata Song(
        int id, string extension, LibraryBuildStatus build = LibraryBuildStatus.Done,
        bool synthetic = false, bool duplicate = false, Guid? owner = null, string artist = "Artist") => new()
    {
        Id = id,
        OwnerUserId = owner ?? Owner,
        SourcePath = $"/src/{id}{extension}",
        FileSizeBytes = 1000,
        FileName = $"{id}{extension}",
        Extension = extension,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        LibraryBuildStatus = build,
        IsSynthetic = synthetic,
        IsDuplicate = duplicate,
        Artist = artist,
        Title = "Song",
    };

    private static UpgradeRequest Request(int id, int songId, UpgradeRequestStatus status, int updatedDaysAgo) => new()
    {
        Id = id,
        SongId = songId,
        OwnerUserId = Owner,
        Status = status,
        Trigger = UpgradeTrigger.Auto,
        CreatedAtUtc = DateTime.UtcNow.AddDays(-updatedDaysAgo),
        UpdatedAtUtc = DateTime.UtcNow.AddDays(-updatedDaysAgo),
    };

    private static MusicHoarderDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private sealed class FakeUpgradeProvider(string name, bool configured) : IUpgradeProvider
    {
        public string Name => name;
        public bool CanUpgrade(UpgradeFloor floor) => configured && floor.Tier < AudioCodecTier.Lossless;
        public Task<DownloadResult> DownloadBetterAsync(DownloadRequest req, UpgradeFloor floor, CancellationToken ct) =>
            Task.FromResult(DownloadResult.Missing("test"));
    }
}
