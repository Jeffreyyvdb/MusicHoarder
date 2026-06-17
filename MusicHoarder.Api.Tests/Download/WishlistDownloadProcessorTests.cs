using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Tests.Download;

public class WishlistDownloadProcessorTests
{
    private static readonly Guid Owner = WellKnownUsers.OwnerId;

    [Fact]
    public async Task ProcessBatch_ExactInLibraryMatch_SkipsOwnedWithoutDownloading()
    {
        await using var db = CreateDbContext();
        db.WishlistItems.Add(MakePending("track-1"));
        db.SpotifyTrackLibraryMatches.Add(new SpotifyTrackLibraryMatch
        {
            OwnerUserId = Owner,
            SpotifyTrackId = "track-1",
            MatchStatus = (int)ComparisonMatchStatus.InLibrary,
            MatchedSongId = 99,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var provider = new FakeDownloadProvider(_ => DownloadResult.Ok("/src/wishlist/x.opus"));
        var processor = CreateProcessor(provider);

        var (processed, downloaded) = await processor.ProcessBatchAsync(db, Owner, default);

        Assert.Equal(1, processed);
        Assert.Equal(0, downloaded);
        Assert.Equal(0, provider.Calls);

        var item = await db.WishlistItems.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(WishlistItemStatus.SkippedOwned, item.Status);
        Assert.Equal(99, item.DownloadedSongId);
    }

    [Fact]
    public async Task ProcessBatch_PossibleMatch_StillDownloads()
    {
        await using var db = CreateDbContext();
        db.WishlistItems.Add(MakePending("track-1"));
        db.SpotifyTrackLibraryMatches.Add(new SpotifyTrackLibraryMatch
        {
            OwnerUserId = Owner,
            SpotifyTrackId = "track-1",
            MatchStatus = (int)ComparisonMatchStatus.PossibleMatch,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var provider = new FakeDownloadProvider(_ => DownloadResult.Ok("/src/wishlist/x.opus"));
        var processor = CreateProcessor(provider);

        var (_, downloaded) = await processor.ProcessBatchAsync(db, Owner, default);

        Assert.Equal(1, downloaded);
        Assert.Equal(1, provider.Calls);
        var item = await db.WishlistItems.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(WishlistItemStatus.Downloaded, item.Status);
    }

    [Fact]
    public async Task ProcessBatch_SuccessfulDownload_SetsDownloadedAndRecordsProvider()
    {
        await using var db = CreateDbContext();
        db.WishlistItems.Add(MakePending("track-1"));
        await db.SaveChangesAsync();

        var provider = new FakeDownloadProvider(_ => DownloadResult.Ok("/src/wishlist/song.opus"));
        var processor = CreateProcessor(provider);

        await processor.ProcessBatchAsync(db, Owner, default);

        var item = await db.WishlistItems.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(WishlistItemStatus.Downloaded, item.Status);
        Assert.Equal("/src/wishlist/song.opus", item.DownloadedFilePath);
        Assert.Equal("fake", item.DownloadProvider);
        Assert.Equal(1, item.AttemptCount);
        Assert.Null(item.LastError);
    }

    [Fact]
    public async Task ProcessBatch_NotFound_SetsNotFoundStatus()
    {
        await using var db = CreateDbContext();
        db.WishlistItems.Add(MakePending("track-1"));
        await db.SaveChangesAsync();

        var processor = CreateProcessor(new FakeDownloadProvider(_ => DownloadResult.Missing("no results")));

        await processor.ProcessBatchAsync(db, Owner, default);

        var item = await db.WishlistItems.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(WishlistItemStatus.NotFound, item.Status);
        Assert.Equal("no results", item.LastError);
    }

    [Fact]
    public async Task ProcessBatch_Failure_SetsFailedStatusAndError()
    {
        await using var db = CreateDbContext();
        db.WishlistItems.Add(MakePending("track-1"));
        await db.SaveChangesAsync();

        var processor = CreateProcessor(new FakeDownloadProvider(_ => DownloadResult.Failed("boom")));

        await processor.ProcessBatchAsync(db, Owner, default);

        var item = await db.WishlistItems.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(WishlistItemStatus.Failed, item.Status);
        Assert.Equal("boom", item.LastError);
        Assert.Equal(1, item.AttemptCount);
    }

    [Fact]
    public async Task ProcessBatch_SkipsDemoUserItems()
    {
        await using var db = CreateDbContext();
        var demoItem = MakePending("track-demo");
        demoItem.OwnerUserId = WellKnownUsers.DemoId;
        db.WishlistItems.Add(demoItem);
        await db.SaveChangesAsync();

        var provider = new FakeDownloadProvider(_ => DownloadResult.Ok("/src/wishlist/x.opus"));
        var processor = CreateProcessor(provider);

        var (processed, _) = await processor.ProcessBatchAsync(db, Owner, default);

        Assert.Equal(0, processed);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task LinkDownloadedItems_LinksItemToSongBySourcePath()
    {
        await using var db = CreateDbContext();
        var item = MakePending("track-1");
        item.Status = WishlistItemStatus.Downloaded;
        item.DownloadedFilePath = "/src/wishlist/song.opus";
        db.WishlistItems.Add(item);
        db.Songs.Add(new SongMetadata
        {
            OwnerUserId = Owner,
            SourcePath = "/src/wishlist/song.opus",
            FileName = "song.opus",
            Extension = ".opus",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var processor = CreateProcessor(new FakeDownloadProvider(_ => DownloadResult.Ok("x")));
        var linked = await processor.LinkDownloadedItemsAsync(db, Owner, default);

        Assert.Equal(1, linked);
        var reloaded = await db.WishlistItems.IgnoreQueryFilters().SingleAsync();
        var song = await db.Songs.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(song.Id, reloaded.DownloadedSongId);
    }

    private static WishlistItem MakePending(string trackId) => new()
    {
        OwnerUserId = Owner,
        SpotifyTrackId = trackId,
        Title = "Title",
        Artist = "Artist",
        Album = "Album",
        DurationMs = 200_000,
        Status = WishlistItemStatus.Pending,
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow,
    };

    private static WishlistDownloadProcessor CreateProcessor(IDownloadProvider provider)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/src",
            DestinationDirectory = "/dest",
            DownloadDirectory = "/downloads",
            DownloadProvider = "fake",
            DownloadConcurrency = 2,
        });
        return new WishlistDownloadProcessor(
            [provider],
            new DownloadProgressTracker(),
            options,
            NullLogger<WishlistDownloadProcessor>.Instance);
    }

    private static MusicHoarderDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }

    private sealed class FakeDownloadProvider(Func<DownloadRequest, DownloadResult> fn) : IDownloadProvider
    {
        private int _calls;
        public int Calls => _calls;

        public string Name => "fake";

        public Task<DownloadResult> DownloadAsync(DownloadRequest req, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(fn(req));
        }
    }
}
