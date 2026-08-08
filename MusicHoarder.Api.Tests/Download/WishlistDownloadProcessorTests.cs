using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Tests.Download;

public class WishlistDownloadProcessorTests : IDisposable
{
    private static readonly Guid Owner = WellKnownUsers.OwnerId;
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private readonly List<string> tempFiles = [];

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
    public async Task ProcessBatch_SuccessfulDownload_StampsFileWithWishlistIdentity()
    {
        await using var db = CreateDbContext();
        var item = MakePending("track-1");
        item.Artist = "¥$";
        item.Title = "PROBLEMATIC";
        item.Album = "VULTURES 1";
        item.Isrc = "USUG12400001";
        db.WishlistItems.Add(item);
        await db.SaveChangesAsync();

        // The provider produces a real file carrying poisoned yt-dlp-style tags; the processor must
        // overwrite them with the wishlist's known Spotify identity before the scanner reads it.
        var produced = CopyFixtureToTemp("silence.mp3");
        using (var seed = TagLib.File.Create(produced))
        {
            seed.Tag.Performers = ["YouTube Channel"];
            seed.Tag.Title = "¥$ - PROBLEMATIC (Official Audio)";
            seed.Save();
        }

        var processor = CreateProcessor(new FakeDownloadProvider(_ => DownloadResult.Ok(produced)));
        await processor.ProcessBatchAsync(db, Owner, default);

        using var file = TagLib.File.Create(produced);
        Assert.Equal(["¥$"], file.Tag.Performers);
        Assert.Equal("PROBLEMATIC", file.Tag.Title);
        Assert.Equal("VULTURES 1", file.Tag.Album);
        Assert.Equal("USUG12400001", file.Tag.ISRC);
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

    [Fact]
    public async Task ResetStaleDownloading_RevertsDownloadingItemsToPending()
    {
        await using var db = CreateDbContext();
        var stuck = MakePending("track-stuck");
        stuck.Status = WishlistItemStatus.Downloading; // leftover from a crash/restart mid-fetch
        var pending = MakePending("track-pending");
        var downloaded = MakePending("track-done");
        downloaded.Status = WishlistItemStatus.Downloaded;
        db.WishlistItems.AddRange(stuck, pending, downloaded);
        await db.SaveChangesAsync();

        var processor = CreateProcessor(new FakeDownloadProvider(_ => DownloadResult.Ok("x")));
        var reset = await processor.ResetStaleDownloadingAsync(db, Owner, default);

        Assert.Equal(1, reset);
        var byTrack = await db.WishlistItems.IgnoreQueryFilters()
            .ToDictionaryAsync(w => w.SpotifyTrackId, w => w.Status);
        Assert.Equal(WishlistItemStatus.Pending, byTrack["track-stuck"]);   // reclaimed
        Assert.Equal(WishlistItemStatus.Pending, byTrack["track-pending"]); // untouched
        Assert.Equal(WishlistItemStatus.Downloaded, byTrack["track-done"]); // untouched
    }

    [Fact]
    public async Task LinkDownloadedItems_ReLinksWhenPreviousSongSoftDeletedAndFileReScanned()
    {
        await using var db = CreateDbContext();
        var deadSong = new SongMetadata
        {
            OwnerUserId = Owner,
            SourcePath = "/src/wishlist/song.opus",
            FileName = "song.opus",
            Extension = ".opus",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            DeletedAtUtc = DateTime.UtcNow, // soft-deleted
        };
        var liveSong = new SongMetadata
        {
            OwnerUserId = Owner,
            SourcePath = "/src/wishlist/song.opus", // same path, re-scanned
            FileName = "song.opus",
            Extension = ".opus",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
        };
        db.Songs.AddRange(deadSong, liveSong);
        await db.SaveChangesAsync();

        var item = MakePending("track-1");
        item.Status = WishlistItemStatus.Downloaded;
        item.DownloadedFilePath = "/src/wishlist/song.opus";
        item.DownloadedSongId = deadSong.Id; // linked to the now-soft-deleted song
        db.WishlistItems.Add(item);
        await db.SaveChangesAsync();

        var processor = CreateProcessor(new FakeDownloadProvider(_ => DownloadResult.Ok("x")));
        var linked = await processor.LinkDownloadedItemsAsync(db, Owner, default);

        Assert.Equal(1, linked);
        var reloaded = await db.WishlistItems.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(liveSong.Id, reloaded.DownloadedSongId); // healed to the live song
    }

    [Fact]
    public async Task LinkDownloadedItems_ClearsDanglingLinkWhenSongSoftDeletedAndNoLiveSong()
    {
        await using var db = CreateDbContext();
        var deadSong = new SongMetadata
        {
            OwnerUserId = Owner,
            SourcePath = "/src/wishlist/gone.opus",
            FileName = "gone.opus",
            Extension = ".opus",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            DeletedAtUtc = DateTime.UtcNow,
        };
        db.Songs.Add(deadSong);
        await db.SaveChangesAsync();

        var item = MakePending("track-1");
        item.Status = WishlistItemStatus.Downloaded;
        item.DownloadedFilePath = "/src/wishlist/gone.opus";
        item.DownloadedSongId = deadSong.Id;
        db.WishlistItems.Add(item);
        await db.SaveChangesAsync();

        var processor = CreateProcessor(new FakeDownloadProvider(_ => DownloadResult.Ok("x")));
        var linked = await processor.LinkDownloadedItemsAsync(db, Owner, default);

        Assert.Equal(0, linked);
        var reloaded = await db.WishlistItems.IgnoreQueryFilters().SingleAsync();
        Assert.Null(reloaded.DownloadedSongId); // dangling link cleared
    }

    // ── Album completion: queue priority + acquisition intent ──────────────────

    [Fact]
    public async Task ProcessBatch_ClaimsUserRequestedBeforeAlbumFill()
    {
        await using var db = CreateDbContext();

        // The album-fill item deliberately carries the *newer* SpotifyAddedAtUtc and the user item a
        // null one — the worst case for the old timestamp-only ordering. Assert on Origin, never on the
        // timestamp: EF emits a plain ORDER BY DESC, Postgres puts NULLS FIRST there, and the in-memory
        // provider used here sorts nulls last, so a timestamp assertion would agree with the test and
        // disagree with production.
        var userItem = MakePending("track-user");
        userItem.SpotifyAddedAtUtc = null;
        var fillItem = MakePending("track-fill");
        fillItem.Origin = WishlistItemOrigin.AlbumCompletion;
        fillItem.SpotifyAddedAtUtc = DateTime.UtcNow;
        db.WishlistItems.AddRange(fillItem, userItem);
        await db.SaveChangesAsync();

        var seen = new List<string>();
        var provider = new FakeDownloadProvider(req =>
        {
            seen.Add(req.Title);
            return DownloadResult.Missing();
        });
        var processor = CreateProcessor(provider, batchSize: 1);

        await processor.ProcessBatchAsync(db, Owner, default);

        var claimed = await db.WishlistItems.IgnoreQueryFilters()
            .Where(w => w.Status != WishlistItemStatus.Pending)
            .SingleAsync();
        Assert.Equal(WishlistItemOrigin.UserRequested, claimed.Origin);

        var stillPending = await db.WishlistItems.IgnoreQueryFilters()
            .Where(w => w.Status == WishlistItemStatus.Pending)
            .SingleAsync();
        Assert.Equal(WishlistItemOrigin.AlbumCompletion, stillPending.Origin);
    }

    [Fact]
    public async Task LinkDownloadedItems_AlbumFillItem_StampsSongAsAlbumFill()
    {
        await using var db = CreateDbContext();
        var song = AddSong(db, "/downloads/fill.opus");
        db.WishlistItems.Add(MakeLinkable(song.SourcePath, WishlistItemOrigin.AlbumCompletion));
        await db.SaveChangesAsync();

        var linked = await CreateProcessor(new FakeDownloadProvider(_ => DownloadResult.Missing()))
            .LinkDownloadedItemsAsync(db, Owner, default);

        Assert.Equal(1, linked);
        var stamped = await db.Songs.IgnoreQueryFilters().SingleAsync(s => s.Id == song.Id);
        Assert.Equal(SongAcquisitionIntent.AlbumFill, stamped.AcquisitionIntent);
    }

    [Fact]
    public async Task LinkDownloadedItems_UserRequestedItem_LeavesSongExplicit()
    {
        await using var db = CreateDbContext();
        var song = AddSong(db, "/downloads/wanted.opus");
        db.WishlistItems.Add(MakeLinkable(song.SourcePath, WishlistItemOrigin.UserRequested));
        await db.SaveChangesAsync();

        await CreateProcessor(new FakeDownloadProvider(_ => DownloadResult.Missing()))
            .LinkDownloadedItemsAsync(db, Owner, default);

        var stamped = await db.Songs.IgnoreQueryFilters().SingleAsync(s => s.Id == song.Id);
        Assert.Equal(SongAcquisitionIntent.Explicit, stamped.AcquisitionIntent);
    }

    [Fact]
    public async Task LinkDownloadedItems_OneUserRequestedLinkAmongFill_PromotesSongToExplicit()
    {
        // Explicit is absorbing: the owner liking an album-fill track on Spotify creates a second,
        // user-requested row pointing at the same song, and that alone moves it into "My music".
        await using var db = CreateDbContext();
        var song = AddSong(db, "/downloads/promoted.opus");
        db.WishlistItems.Add(MakeLinkable(song.SourcePath, WishlistItemOrigin.AlbumCompletion));
        db.WishlistItems.Add(MakeLinkable(song.SourcePath, WishlistItemOrigin.UserRequested, "track-liked"));
        await db.SaveChangesAsync();

        await CreateProcessor(new FakeDownloadProvider(_ => DownloadResult.Missing()))
            .LinkDownloadedItemsAsync(db, Owner, default);

        var stamped = await db.Songs.IgnoreQueryFilters().SingleAsync(s => s.Id == song.Id);
        Assert.Equal(SongAcquisitionIntent.Explicit, stamped.AcquisitionIntent);
    }

    [Fact]
    public async Task ProcessBatch_SkippedOwnedUserItem_PromotesAlbumFillSongToExplicit()
    {
        // The SkippedOwned path links straight to an existing song without going through the linker.
        await using var db = CreateDbContext();
        var song = AddSong(db, "/downloads/already-here.opus");
        song.AcquisitionIntent = SongAcquisitionIntent.AlbumFill;

        // The album-fill row that originally brought the file in, already linked.
        var fill = MakeLinkable(song.SourcePath, WishlistItemOrigin.AlbumCompletion);
        fill.DownloadedSongId = song.Id;
        db.WishlistItems.Add(fill);

        // The owner then likes it on Spotify: a new user-requested row that resolves to SkippedOwned.
        db.WishlistItems.Add(MakePending("track-liked"));
        db.SpotifyTrackLibraryMatches.Add(new SpotifyTrackLibraryMatch
        {
            OwnerUserId = Owner,
            SpotifyTrackId = "track-liked",
            MatchStatus = (int)ComparisonMatchStatus.InLibrary,
            MatchedSongId = song.Id,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        await CreateProcessor(new FakeDownloadProvider(_ => DownloadResult.Missing()))
            .ProcessBatchAsync(db, Owner, default);

        var stamped = await db.Songs.IgnoreQueryFilters().SingleAsync(s => s.Id == song.Id);
        Assert.Equal(SongAcquisitionIntent.Explicit, stamped.AcquisitionIntent);
    }

    private static SongMetadata AddSong(MusicHoarderDbContext db, string sourcePath)
    {
        var song = new SongMetadata
        {
            OwnerUserId = Owner,
            SourcePath = sourcePath,
            FileName = Path.GetFileName(sourcePath),
            Extension = Path.GetExtension(sourcePath),
            FileSizeBytes = 1024,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
        };
        db.Songs.Add(song);
        db.SaveChanges();
        return song;
    }

    private static WishlistItem MakeLinkable(
        string downloadedFilePath, WishlistItemOrigin origin, string? spotifyTrackId = null) => new()
        {
            OwnerUserId = Owner,
            SpotifyTrackId = spotifyTrackId,
            Origin = origin,
            Title = "Title",
            Artist = "Artist",
            Album = "Album",
            DurationMs = 200_000,
            Status = WishlistItemStatus.Downloaded,
            DownloadedFilePath = downloadedFilePath,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };

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

    private static WishlistDownloadProcessor CreateProcessor(IDownloadProvider provider, int batchSize = 20)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/src",
            DestinationDirectory = "/dest",
            DownloadDirectory = "/downloads",
            DownloadProvider = "fake",
            DownloadConcurrency = 2,
            WishlistDownloadBatchSize = batchSize,
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

    private string CopyFixtureToTemp(string fixtureName)
    {
        var source = Path.Combine(FixtureDir, fixtureName);
        var dest = Path.Combine(
            Path.GetTempPath(),
            $"mh-wishdl-{Guid.NewGuid():N}{Path.GetExtension(fixtureName)}");
        File.Copy(source, dest, overwrite: true);
        tempFiles.Add(dest);
        return dest;
    }

    public void Dispose()
    {
        foreach (var f in tempFiles)
        {
            try { File.Delete(f); } catch { /* best effort */ }
        }
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
