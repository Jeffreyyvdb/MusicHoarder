using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Artwork;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Pipeline;
using MusicHoarder.Api.Storage;

namespace MusicHoarder.Api.Tests.Storage;

public class StorageUsageCalculatorTests
{
    private const string Source = "/source";
    private const string Dest = "/dest";
    private const string Downloads = "/downloads";
    private const string Synced = "/downloads/synced";
    private const string Cache = "/cache";

    [Fact]
    public async Task Counts_source_and_destination_copies_by_their_on_disk_length()
    {
        await using var db = NewDb();
        // FileSizeBytes is deliberately wrong: the measurement must come from the disk, not the row.
        db.Songs.Add(Built("/source/a.flac", "/dest/A/a.flac", size: 999));
        await db.SaveChangesAsync();
        var fs = Fs(("/source/a.flac", "aaaa"), ("/dest/A/a.flac", "bbbbbb"));

        var snapshot = await Create(db, fs).ComputeAsync();

        Assert.Equal((4, 1), Bucket(snapshot.Categories, CategoryKeys.Source));
        Assert.Equal((6, 1), Bucket(snapshot.Categories, CategoryKeys.Library));
        Assert.Equal(10, snapshot.ManagedBytes);
        Assert.Equal((6, 1), Bucket(snapshot.Origins, OriginKeys.Local));
        Assert.Equal((0, 0), Bucket(snapshot.Categories, CategoryKeys.Untracked));
    }

    [Fact]
    public async Task Released_source_is_no_longer_claimed_so_a_leftover_file_is_untracked()
    {
        await using var db = NewDb();
        db.Songs.Add(Built("/downloads/r.flac", "/dest/R/r.flac", released: true));
        await db.SaveChangesAsync();
        var fs = Fs(("/downloads/r.flac", "leftover"), ("/dest/R/r.flac", "library!"));

        var snapshot = await Create(db, fs).ComputeAsync();

        Assert.Equal((8, 1), Bucket(snapshot.Categories, CategoryKeys.Library));
        Assert.Equal((0, 0), Bucket(snapshot.Categories, CategoryKeys.Staging));
        Assert.Equal((8, 1), Bucket(snapshot.Categories, CategoryKeys.Untracked));
        var download = snapshot.Roots.Single(r => r.Key == RootKeys.Download);
        Assert.Equal(8, download.UntrackedAudioBytes);
        Assert.Equal(1, download.UntrackedAudioFiles);
    }

    [Fact]
    public async Task Soft_deleted_rows_do_not_claim_their_destination_file()
    {
        await using var db = NewDb();
        var song = Built("/source/d.flac", "/dest/D/d.flac");
        song.SoftDelete();
        db.Songs.Add(song);
        await db.SaveChangesAsync();
        var fs = Fs(("/dest/D/d.flac", "orphan"));

        var snapshot = await Create(db, fs).ComputeAsync();

        Assert.Equal((0, 0), Bucket(snapshot.Categories, CategoryKeys.Library));
        Assert.Equal((6, 1), Bucket(snapshot.Categories, CategoryKeys.Untracked));
    }

    [Fact]
    public async Task A_retag_in_flight_keeps_both_destination_paths_tracked()
    {
        await using var db = NewDb();
        var retag = Built("/source/t.flac", "/dest/T/t.flac");
        retag.RequeueForRetag(); // Pending again, DestinationPath kept, PreviousDestinationPath set
        var moved = Built("/source/m.flac", "/dest/M/new.flac");
        moved.PreviousDestinationPath = "/dest/M/old.flac";
        db.Songs.AddRange(retag, moved);
        await db.SaveChangesAsync();
        var fs = Fs(("/dest/T/t.flac", "tt"), ("/dest/M/new.flac", "nnn"), ("/dest/M/old.flac", "oooo"));

        var snapshot = await Create(db, fs).ComputeAsync();

        Assert.Equal((9, 3), Bucket(snapshot.Categories, CategoryKeys.Library));
        Assert.Equal((0, 0), Bucket(snapshot.Categories, CategoryKeys.Untracked));
    }

    [Fact]
    public async Task Duplicate_rows_count_in_their_category_and_in_duplicates()
    {
        await using var db = NewDb();
        var dup = Built("/source/x.flac", "/dest/X/x.flac");
        dup.IsDuplicate = true;
        db.Songs.AddRange(dup, Built("/source/y.flac", "/dest/Y/y.flac"));
        await db.SaveChangesAsync();
        var fs = Fs(("/source/x.flac", "xx"), ("/dest/X/x.flac", "xxx"), ("/source/y.flac", "y"), ("/dest/Y/y.flac", "yy"));

        var snapshot = await Create(db, fs).ComputeAsync();

        Assert.Equal((3, 2), Bucket(snapshot.Categories, CategoryKeys.Source));
        Assert.Equal((5, 2), Bucket(snapshot.Categories, CategoryKeys.Library));
        Assert.Equal(5, snapshot.Duplicates.Bytes); // both on-disk copies of the flagged row
        Assert.Equal(1, snapshot.Duplicates.Tracks);
    }

    [Fact]
    public async Task Origins_follow_wishlist_links_then_the_root_the_file_came_from()
    {
        await using var db = NewDb();
        var liked = new WishlistSource { OwnerUserId = WellKnownUsers.OwnerId, SourceType = WishlistSourceType.LikedSongs, Name = "Liked Songs" };
        var playlist = new WishlistSource { OwnerUserId = WellKnownUsers.OwnerId, SourceType = WishlistSourceType.Playlist, Name = "Mix" };
        var deezer = new WishlistSource { OwnerUserId = WellKnownUsers.OwnerId, SourceType = WishlistSourceType.DeezerPlaylist, Name = "Deezer" };
        var youTube = new WishlistSource { OwnerUserId = WellKnownUsers.OwnerId, SourceType = WishlistSourceType.YouTubePlaylist, Name = "Cool music" };
        db.WishlistSources.AddRange(liked, playlist, deezer, youTube);

        var fromLiked = Built("/downloads/1.opus", "/dest/1/1.opus");
        var fromPlaylist = Built("/downloads/2.opus", "/dest/2/2.opus");
        var fromDeezer = Built("/downloads/3.opus", "/dest/3/3.opus");
        var fromUrl = Built("/downloads/4.opus", "/dest/4/4.opus");
        var fromCompletion = Built("/downloads/5.opus", "/dest/5/5.opus");
        var otherDownload = Built("/downloads/6.flac", "/dest/6/6.flac");
        var synced = Built("/downloads/synced/7.flac", "/dest/7/7.flac");
        var local = Built("/source/8.flac", "/dest/8/8.flac");
        var fromYouTube = Built("/downloads/9.opus", "/dest/9/9.opus");
        db.Songs.AddRange(fromLiked, fromPlaylist, fromDeezer, fromUrl, fromCompletion, otherDownload, synced, local, fromYouTube);
        db.WishlistItems.AddRange(
            Item(liked, fromLiked),
            Item(playlist, fromPlaylist),
            Item(deezer, fromDeezer),
            Item(null, fromUrl, sourceUrl: "https://youtube.com/watch?v=x"),
            Item(null, fromCompletion, origin: WishlistItemOrigin.AlbumCompletion),
            // A playlist video also carries its URL; the source, not the link, is why it is here.
            Item(youTube, fromYouTube, sourceUrl: "https://www.youtube.com/watch?v=y"));
        await db.SaveChangesAsync();

        // Distinct lengths per destination file, so a misattribution shows up as the wrong number.
        var fs = Fs(
            ("/dest/1/1.opus", "1"), ("/dest/2/2.opus", "22"), ("/dest/3/3.opus", "333"), ("/dest/4/4.opus", "4444"),
            ("/dest/5/5.opus", "55555"), ("/dest/6/6.flac", "666666"), ("/dest/7/7.flac", "7777777"), ("/dest/8/8.flac", "88888888"),
            ("/dest/9/9.opus", "999999999"));

        var snapshot = await Create(db, fs).ComputeAsync();

        Assert.Equal((1, 1), Bucket(snapshot.Origins, OriginKeys.SpotifyLiked));
        Assert.Equal((2, 1), Bucket(snapshot.Origins, OriginKeys.SpotifyPlaylist));
        Assert.Equal((3, 1), Bucket(snapshot.Origins, OriginKeys.DeezerPlaylist));
        Assert.Equal((4, 1), Bucket(snapshot.Origins, OriginKeys.DirectUrl));
        Assert.Equal((5, 1), Bucket(snapshot.Origins, OriginKeys.AlbumCompletion));
        Assert.Equal((6, 1), Bucket(snapshot.Origins, OriginKeys.OtherDownload));
        Assert.Equal((7, 1), Bucket(snapshot.Origins, OriginKeys.Synced));
        Assert.Equal((8, 1), Bucket(snapshot.Origins, OriginKeys.Local));
        Assert.Equal((9, 1), Bucket(snapshot.Origins, OriginKeys.YouTubePlaylist));
        Assert.Equal(snapshot.Origins.Sum(o => o.Bytes), Bucket(snapshot.Categories, CategoryKeys.Library).Bytes);
    }

    [Fact]
    public async Task Nested_roots_are_walked_once_and_files_go_to_the_innermost_root()
    {
        await using var db = NewDb();
        db.Songs.Add(Built("/downloads/synced/s.flac", "/dest/S/s.flac"));
        await db.SaveChangesAsync();
        var fs = Fs(
            ("/downloads/videos/clip.mp4", "video-bytes"),          // videos root nested in downloads
            ("/downloads/videos/clip.jpg", "thumb"),
            ("/downloads/synced/s.flac", "synced!"),                 // synced root nested in downloads
            ("/downloads/synced/.incoming/half.flac", "partial"),   // dot-dir → temp, never "untracked"
            ("/dest/Playlists/Mix.m3u8", "#EXTM3U"),                // playlists live under destination
            ("/dest/S/s.flac", "library"),
            ("/dest/S/cover.jpg", "cover-bytes"),
            ("/dest/S/album.nfo", "nfo"),
            ("/cache/ab/abcd.webp", "webp!"));

        var snapshot = await Create(db, fs).ComputeAsync();

        Assert.Equal((16, 2), Bucket(snapshot.Categories, CategoryKeys.Videos));
        Assert.Equal((7, 1), Bucket(snapshot.Categories, CategoryKeys.Synced));
        Assert.Equal((7, 1), Bucket(snapshot.Categories, CategoryKeys.Temp));
        Assert.Equal((7, 1), Bucket(snapshot.Categories, CategoryKeys.Playlists));
        Assert.Equal((7, 1), Bucket(snapshot.Categories, CategoryKeys.Library));
        Assert.Equal((11, 1), Bucket(snapshot.Categories, CategoryKeys.Covers));
        Assert.Equal((3, 1), Bucket(snapshot.Categories, CategoryKeys.Other));
        Assert.Equal((5, 1), Bucket(snapshot.Categories, CategoryKeys.ThumbnailCache));
        Assert.Equal((0, 0), Bucket(snapshot.Categories, CategoryKeys.Untracked));
        Assert.Equal((0, 0), Bucket(snapshot.Categories, CategoryKeys.Staging));

        var download = snapshot.Roots.Single(r => r.Key == RootKeys.Download);
        var videos = snapshot.Roots.Single(r => r.Key == RootKeys.Videos);
        var synced = snapshot.Roots.Single(r => r.Key == RootKeys.Synced);
        Assert.True(download.Walked);
        Assert.True(videos.Walked);
        Assert.True(synced.Walked);
        Assert.Equal(16, videos.Bytes);
        Assert.Equal(14, synced.Bytes);
        Assert.Equal(0, download.Bytes); // nothing directly under it — the nested roots own their files
    }

    [Fact]
    public async Task Demo_tenant_and_synthetic_rows_are_not_indexed()
    {
        await using var db = NewDb();
        var demo = Built("/source/demo.flac", "/dest/Demo/demo.flac");
        demo.OwnerUserId = WellKnownUsers.DemoId;
        var synthetic = Built("/source/synth.flac", "/dest/Synth/synth.flac");
        synthetic.IsSynthetic = true;
        db.Songs.AddRange(demo, synthetic);
        await db.SaveChangesAsync();
        var fs = Fs(("/dest/Demo/demo.flac", "demo"), ("/dest/Synth/synth.flac", "synth"));

        var snapshot = await Create(db, fs).ComputeAsync();

        Assert.Equal((0, 0), Bucket(snapshot.Categories, CategoryKeys.Library));
        Assert.Equal((9, 2), Bucket(snapshot.Categories, CategoryKeys.Untracked));
    }

    [Fact]
    public async Task Rows_are_still_indexed_when_the_ambient_user_is_anonymous()
    {
        // A hosted-service scope has a current-user accessor that answers Guid.Empty; the ambient
        // tenant filter then matches nothing. The calculator must bypass it or every file looks orphaned.
        var dbOptions = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new MusicHoarderDbContext(dbOptions, new StubCurrentUser(Guid.Empty));
        var liked = new WishlistSource { OwnerUserId = WellKnownUsers.OwnerId, SourceType = WishlistSourceType.LikedSongs, Name = "Liked Songs" };
        var song = Built("/downloads/l.opus", "/dest/L/l.opus");
        db.WishlistSources.Add(liked);
        db.Songs.Add(song);
        db.WishlistItems.Add(Item(liked, song));
        await db.SaveChangesAsync();
        var fs = Fs(("/downloads/l.opus", "staged"), ("/dest/L/l.opus", "library"));

        var snapshot = await Create(db, fs).ComputeAsync();

        Assert.Equal((7, 1), Bucket(snapshot.Categories, CategoryKeys.Library));
        Assert.Equal((6, 1), Bucket(snapshot.Categories, CategoryKeys.Staging));
        Assert.Equal((7, 1), Bucket(snapshot.Origins, OriginKeys.SpotifyLiked));
        Assert.Equal((0, 0), Bucket(snapshot.Categories, CategoryKeys.Untracked));
    }

    [Fact]
    public async Task Missing_roots_are_reported_and_an_offline_source_is_not_probed()
    {
        await using var db = NewDb();
        var fs = Fs(("/source/a.flac", "a"), ("/dest/keep", "x"));

        var snapshot = await Create(
            db, fs,
            options: Options(o => o.DownloadDirectory = "/nowhere"),
            availability: new StubAvailability(sourceAvailable: false, destinationAvailable: true)).ComputeAsync();

        var source = snapshot.Roots.Single(r => r.Key == RootKeys.Source);
        Assert.Equal(StorageUsageCalculator.SkipOffline, source.Skipped);
        Assert.False(source.Walked);
        Assert.Equal((0, 0), Bucket(snapshot.Categories, CategoryKeys.Untracked)); // its audio was never read

        var download = snapshot.Roots.Single(r => r.Key == RootKeys.Download);
        Assert.False(download.Exists);
        Assert.False(download.Walked);
        Assert.Null(download.Skipped);

        Assert.True(snapshot.Roots.Single(r => r.Key == RootKeys.Destination).Walked);
    }

    [Fact]
    public async Task Volumes_dedupe_identical_figures_and_capacity_sums_the_distinct_ones()
    {
        await using var db = NewDb();
        var fs = Fs(("/source/a", "a"), ("/dest/b", "b"), ("/downloads/c", "c"));
        var probe = new FakeVolumeProbe(path => path switch
        {
            Source or Dest => new VolumeCapacity(1000, 100), // two bind mounts of one filesystem
            Downloads => new VolumeCapacity(5000, 500),
            _ => null,                                        // cache dir: unprobeable
        });

        var snapshot = await Create(db, fs, volumes: probe).ComputeAsync();

        Assert.Equal(2, snapshot.Volumes.Count);
        Assert.Equal(6000, snapshot.CapacityBytes);
        Assert.Equal(600, snapshot.FreeBytes);
        Assert.Equal(Source, snapshot.Volumes[0].SamplePath);
    }

    [Fact]
    public async Task Reclaimable_reports_why_the_staged_release_is_unavailable()
    {
        await using var db = NewDb();

        var snapshot = await Create(db, Fs(), options: Options(o => o.EnableWishlistDownloads = false)).ComputeAsync();

        Assert.Equal(StagedSourceReleaseResult.IdleDownloadsDisabled, snapshot.Reclaimable.UnavailableReason);
        Assert.Equal(0, snapshot.Reclaimable.StagedSourceBytes);
    }

    [Fact]
    public async Task Lyrics_report_text_length_over_rows_that_have_any()
    {
        await using var db = NewDb();
        var withLyrics = Built("/source/l.flac", "/dest/L/l.flac");
        withLyrics.PlainLyrics = "hello";
        withLyrics.SyncedLyrics = "[00:01.00] hello";
        db.Songs.AddRange(withLyrics, Built("/source/n.flac", "/dest/N/n.flac"));
        await db.SaveChangesAsync();

        var snapshot = await Create(db, Fs()).ComputeAsync();

        Assert.Equal(1, snapshot.Lyrics.TracksWithLyrics);
        Assert.Equal(5 + 16, snapshot.Lyrics.ApproxTextBytes);
    }

    // ── store ────────────────────────────────────────────────────────────────

    [Fact]
    public void Store_is_single_flight_and_reports_a_queued_request_as_computing()
    {
        var store = new StorageUsageSnapshotStore();
        Assert.False(store.Get().Computing);

        Assert.True(store.RequestRefresh());
        Assert.True(store.RequestRefresh()); // idempotent while queued
        Assert.True(store.Get().Computing);  // queued reads as computing
        Assert.True(store.WaitForRequestAsync(CancellationToken.None).IsCompletedSuccessfully);

        Assert.True(store.TryBegin());
        Assert.False(store.TryBegin());
        Assert.False(store.RequestRefresh()); // refused while running
        Assert.True(store.IsComputing);

        store.Fail("boom");
        Assert.False(store.IsComputing);
        Assert.Equal("boom", store.Get().LastError);
        Assert.False(store.Get().Computing);
    }

    [Fact]
    public async Task Store_still_reports_computing_after_the_request_is_consumed_but_before_the_measurement_begins()
    {
        // The background service wakes on the semaphore, then calls TryBegin a moment later. An endpoint
        // that reads the store in between must still say a measurement is coming, or the client stops
        // polling and keeps the stale snapshot.
        var store = new StorageUsageSnapshotStore();
        Assert.True(store.RequestRefresh());
        await store.WaitForRequestAsync(CancellationToken.None);

        Assert.True(store.Get().Computing);
        Assert.True(store.TryBegin());
        Assert.True(store.Get().Computing);

        store.Complete(new StorageUsageSnapshot(
            DateTime.UtcNow, 0, 0, 0, 0, [], [], [], [], new StorageDuplicates(0, 0), new StorageLyrics(0, 0), new StorageReclaimable(0, 0, null)));
        Assert.False(store.Get().Computing);
        Assert.NotNull(store.Get().Snapshot);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static (long Bytes, int Files) Bucket(IReadOnlyList<StorageBucket> buckets, string key)
    {
        var bucket = buckets.Single(b => b.Key == key);
        return (bucket.Bytes, bucket.Files);
    }

    private static SongMetadata Built(string source, string destination, long size = 12, bool released = false)
    {
        var song = new SongMetadata
        {
            OwnerUserId = WellKnownUsers.OwnerId,
            SourcePath = source,
            FileName = Path.GetFileName(source),
            Extension = Path.GetExtension(source),
            FileSizeBytes = size,
            LastModifiedUtc = DateTime.UtcNow.AddDays(-1),
            IndexedAtUtc = DateTime.UtcNow.AddDays(-1),
            DurationMs = 180_000,
            DurationSeconds = 180,
            Artist = "Artist",
            Album = "Album",
            Title = "Track",
            EnrichmentStatus = EnrichmentStatus.Matched,
        };
        song.MarkBuildDone(destination);
        song.LibraryBuiltAtUtc = DateTime.UtcNow.AddHours(-1);
        if (released) song.MarkSourceReleased();
        return song;
    }

    private static WishlistItem Item(
        WishlistSource? source,
        SongMetadata song,
        string? sourceUrl = null,
        WishlistItemOrigin origin = WishlistItemOrigin.UserRequested) => new()
    {
        OwnerUserId = WellKnownUsers.OwnerId,
        WishlistSource = source,
        Title = song.Title ?? "Track",
        Artist = song.Artist ?? "Artist",
        SourceUrl = sourceUrl,
        Origin = origin,
        Status = WishlistItemStatus.Downloaded,
        DownloadedSong = song,
    };

    private static MockFileSystem Fs(params (string Path, string Content)[] files)
    {
        var fs = new MockFileSystem(files.ToDictionary(f => f.Path, f => new MockFileData(f.Content)));
        // Roots exist as directories even when empty, so a test can measure "nothing there".
        foreach (var root in new[] { Source, Dest, Downloads, Cache })
            fs.AddDirectory(root);
        return fs;
    }

    private static IOptions<MusicEnricherOptions> Options(Action<MusicEnricherOptions>? mutate = null)
    {
        var o = new MusicEnricherOptions
        {
            SourceDirectory = Source,
            DestinationDirectory = Dest,
            DownloadDirectory = Downloads,
            EnableWishlistDownloads = true,
        };
        mutate?.Invoke(o);
        return Microsoft.Extensions.Options.Options.Create(o);
    }

    private static StorageUsageCalculator Create(
        MusicHoarderDbContext db,
        IFileSystem fs,
        IOptions<MusicEnricherOptions>? options = null,
        IStorageVolumeProbe? volumes = null,
        IDirectoryAvailability? availability = null)
    {
        options ??= Options();
        var sync = new TestOptionsMonitor<SyncOptions>(new SyncOptions { SyncedSourceDirectory = Synced });
        var stagedRelease = new StagedSourceReleaseService(
            db, fs, new FakeProbe(), options, sync, new StagedSourceReleaseTracker(),
            NullLogger<StagedSourceReleaseService>.Instance);
        return new StorageUsageCalculator(
            db,
            fs,
            options,
            sync,
            Microsoft.Extensions.Options.Options.Create(new SlskdOptions()),
            new StubThumbnails(Cache),
            availability ?? new StubAvailability(sourceAvailable: true, destinationAvailable: true),
            volumes ?? new FakeVolumeProbe(_ => null),
            stagedRelease,
            new OwnerLookupService(),
            NullLogger<StorageUsageCalculator>.Instance);
    }

    private static MusicHoarderDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }

    private sealed class StubCurrentUser(Guid userId) : ICurrentUserAccessor
    {
        public CurrentUser? User => null;
        public Guid UserId => userId;
    }

    private sealed class StubThumbnails(string directory) : ICoverThumbnailService
    {
        public string CacheDirectory => directory;
        public int ClampToBucket(int requestedSize) => requestedSize;
        public Task<ResolvedCover?> GetThumbnailAsync(ResolvedCover source, string identityPath, int size, CancellationToken ct = default)
            => Task.FromResult<ResolvedCover?>(null);
    }

    private sealed class StubAvailability(bool sourceAvailable, bool destinationAvailable) : IDirectoryAvailability
    {
        public DirectoryAvailabilitySnapshot Current { get; } =
            new(sourceAvailable, destinationAvailable, Source, Dest, DateTime.UtcNow);
        public Task<DirectoryAvailabilitySnapshot> ProbeNowAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Current);
    }

    private sealed class FakeVolumeProbe(Func<string, VolumeCapacity?> probe) : IStorageVolumeProbe
    {
        public VolumeCapacity? Probe(string directoryPath) => probe(directoryPath);
    }

    private sealed class FakeProbe : IAudioFileProbe
    {
        public AudioProbeResult? Probe(string path) => new(180_000);
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
