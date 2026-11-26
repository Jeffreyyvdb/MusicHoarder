using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
    public async Task Sweep_DeferredRequestBlocksOnlyForShortWindow()
    {
        await using var db = CreateDbContext();
        db.Songs.AddRange(Song(1, ".opus"), Song(2, ".opus"), Song(3, ".opus"));
        db.UpgradeRequests.AddRange(
            Request(1, songId: 1, UpgradeRequestStatus.Deferred, updatedHoursAgo: 2),   // provider was down; still inside the 6h window
            Request(2, songId: 2, UpgradeRequestStatus.Deferred, updatedHoursAgo: 30),  // eligible again
            Request(3, songId: 3, UpgradeRequestStatus.Failed, updatedHoursAgo: 5 * 24)); // a real failure keeps the 30-day cooldown
        await db.SaveChangesAsync();

        var queued = await CreateSweep(db, new QualityUpgradeChannel()).SweepAsync(default);

        Assert.Equal(1, queued);
        var newRequest = await db.UpgradeRequests.Where(r => r.Status == UpgradeRequestStatus.Queued).SingleAsync();
        Assert.Equal(2, newRequest.SongId);
    }

    [Fact]
    public async Task Sweep_PrioritisesSongsDeliveredByAFallbackProvider()
    {
        await using var db = CreateDbContext();
        db.Songs.AddRange(Song(1, ".opus"), Song(2, ".opus"), Song(50, ".opus"));
        // Song 50 was fetched by yt-dlp because spotiflac was unreachable at the time.
        db.WishlistItems.Add(new WishlistItem
        {
            OwnerUserId = Owner,
            SpotifyTrackId = "t50",
            Title = "Song",
            Artist = "Artist",
            Status = WishlistItemStatus.Downloaded,
            DownloadProvider = "yt-dlp",
            FallbackFromProvider = "spotiflac",
            DownloadedSongId = 50,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var queued = await CreateSweep(db, new QualityUpgradeChannel(), batchSize: 1).SweepAsync(default);

        Assert.Equal(1, queued);
        Assert.Equal(50, (await db.UpgradeRequests.SingleAsync()).SongId); // ahead of the Id walk
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

    [Fact]
    public async Task Sweep_StreamingOnlyProvider_SkipsUnreleasedAndIdentitylessSongs()
    {
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            Song(1, ".opus", spotifyId: "sp-1"),                              // eligible: has SpotifyId
            Song(2, ".opus", isrc: "USRC17607839"),                           // eligible: has ISRC
            Song(3, ".opus"),                                                 // skipped: neither SpotifyId nor ISRC
            Song(4, ".opus", spotifyId: "   ", isrc: "  "),                   // skipped: whitespace-only ids
            Song(5, ".opus", spotifyId: "sp-5", isUnreleased: true));         // skipped: unreleased
        await db.SaveChangesAsync();

        var spotiflac = new FakeUpgradeProvider("spotiflac", configured: true, requiresStreamingIdentity: true);
        // Unconfigured slskd in the chain must not disable the streaming-only filter.
        var unconfiguredSlskd = new FakeUpgradeProvider("slskd", configured: false, requiresStreamingIdentity: false);
        var sweep = CreateSweep(db, new QualityUpgradeChannel(), chain: ["spotiflac", "slskd", "yt-dlp"],
            providers: [spotiflac, unconfiguredSlskd], downloadProviders: [new FakeDownloadOnlyProvider("yt-dlp")]);

        var queued = await sweep.SweepAsync(default);

        Assert.Equal(2, queued);
        var queuedIds = await db.UpgradeRequests.OrderBy(r => r.SongId).Select(r => r.SongId).ToListAsync();
        Assert.Equal([1, 2], queuedIds);
    }

    [Fact]
    public async Task Sweep_WithNonStreamingProviderAlsoActive_QueuesIdentitylessSongs()
    {
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            Song(1, ".opus", spotifyId: "sp-1"),
            Song(2, ".opus"),                                                 // slskd can still search by artist+title
            Song(3, ".opus", isUnreleased: true));                            // slskd can still search leaks
        await db.SaveChangesAsync();

        var spotiflac = new FakeUpgradeProvider("spotiflac", configured: true, requiresStreamingIdentity: true);
        var slskd = new FakeUpgradeProvider("slskd", configured: true, requiresStreamingIdentity: false);
        var sweep = CreateSweep(db, new QualityUpgradeChannel(), chain: ["spotiflac", "slskd", "yt-dlp"],
            providers: [spotiflac, slskd], downloadProviders: [new FakeDownloadOnlyProvider("yt-dlp")]);

        var queued = await sweep.SweepAsync(default);

        Assert.Equal(3, queued);
    }

    [Fact]
    public async Task Sweep_SkipsDownloadOnlyProvidersWithoutWarning()
    {
        await using var db = CreateDbContext();
        db.Songs.Add(Song(1, ".opus", spotifyId: "sp-1"));
        await db.SaveChangesAsync();

        var logger = new RecordingLogger<AutomaticUpgradeSweep>();
        var spotiflac = new FakeUpgradeProvider("spotiflac", configured: true, requiresStreamingIdentity: true);
        var sweep = CreateSweep(db, new QualityUpgradeChannel(), chain: ["spotiflac", "yt-dlp"],
            providers: [spotiflac], downloadProviders: [new FakeDownloadOnlyProvider("yt-dlp")], logger: logger);

        var queued = await sweep.SweepAsync(default);

        Assert.Equal(1, queued);
        Assert.DoesNotContain(logger.Messages, m => m.Level == LogLevel.Warning);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static AutomaticUpgradeSweep CreateSweep(
        MusicHoarderDbContext db, QualityUpgradeChannel channel,
        bool enabled = true, bool providerConfigured = true, int batchSize = 50) =>
        CreateSweep(
            db, channel, chain: ["fake"],
            providers: [new FakeUpgradeProvider("fake", providerConfigured)],
            downloadProviders: [],
            enabled: enabled, batchSize: batchSize);

    private static AutomaticUpgradeSweep CreateSweep(
        MusicHoarderDbContext db,
        QualityUpgradeChannel channel,
        string[] chain,
        IUpgradeProvider[] providers,
        IDownloadProvider[] downloadProviders,
        bool enabled = true,
        int batchSize = 50,
        ILogger<AutomaticUpgradeSweep>? logger = null)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/src",
            DestinationDirectory = "/dest",
            DownloadProviders = chain,
            EnableAutomaticQualityUpgrades = enabled,
            QualityUpgradeCooldownDays = 30,
            QualityUpgradeDeferredRetryHours = 6,
            QualityUpgradeBatchSize = batchSize,
        });
        return new AutomaticUpgradeSweep(
            db, providers, downloadProviders, channel, new OwnerLookupService(), options,
            logger ?? NullLogger<AutomaticUpgradeSweep>.Instance);
    }

    private static SongMetadata Song(
        int id, string extension, LibraryBuildStatus build = LibraryBuildStatus.Done,
        bool synthetic = false, bool duplicate = false, Guid? owner = null, string artist = "Artist",
        string? spotifyId = null, string? isrc = null, bool isUnreleased = false) => new()
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
        IsUnreleased = isUnreleased,
        SpotifyId = spotifyId,
        Isrc = isrc,
        Artist = artist,
        Title = "Song",
    };

    private static UpgradeRequest Request(int id, int songId, UpgradeRequestStatus status, int updatedDaysAgo = 0, int updatedHoursAgo = 0)
    {
        var updated = DateTime.UtcNow.AddDays(-updatedDaysAgo).AddHours(-updatedHoursAgo);
        return new()
        {
            Id = id,
            SongId = songId,
            OwnerUserId = Owner,
            Status = status,
            Trigger = UpgradeTrigger.Auto,
            CreatedAtUtc = updated,
            UpdatedAtUtc = updated,
        };
    }

    private static MusicHoarderDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private sealed class FakeUpgradeProvider(
        string name, bool configured, bool requiresStreamingIdentity = false) : IUpgradeProvider
    {
        public string Name => name;
        public bool RequiresStreamingIdentity => requiresStreamingIdentity;
        public bool CanUpgrade(UpgradeFloor floor) => configured && floor.Tier < AudioCodecTier.Lossless;
        public Task<DownloadResult> DownloadBetterAsync(DownloadRequest req, UpgradeFloor floor, CancellationToken ct) =>
            Task.FromResult(DownloadResult.Missing("test"));
    }
}
