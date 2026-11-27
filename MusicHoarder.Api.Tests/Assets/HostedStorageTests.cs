using System.Collections;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Assets;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;
using MusicHoarder.Api.Tests.Download;
using MusicHoarder.Api.Tests.Endpoints;
using MusicHoarder.Api.Tests.Persistence;

namespace MusicHoarder.Api.Tests.Assets;

/// <summary>
/// Hosted storage mode and audio assets: one file per distinct set of bytes, downloads stored by hash,
/// builds that neither copy nor tag, and adoption that registers files where they already are. Folder
/// mode must come out of all of it unchanged.
/// </summary>
public sealed class HostedStorageTests : IDisposable
{
    private static readonly Guid Owner = WellKnownUsers.OwnerId;
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mh-hosted-{Guid.NewGuid():N}");
    private readonly string _databaseName = Guid.NewGuid().ToString("N");

    private string Source => Path.Combine(_root, "source");
    private string Downloads => Path.Combine(_root, "downloads");
    private string Destination => Path.Combine(_root, "dest");

    public HostedStorageTests()
    {
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Downloads);
        Directory.CreateDirectory(Destination);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Identical_bytes_are_one_asset_whichever_file_and_context_they_come_from()
    {
        var first = CopyFixture("silence.flac", Path.Combine(Source, "a", "one.flac"));
        var second = CopyFixture("silence.flac", Path.Combine(Source, "b", "two.flac"));
        var third = CopyFixture("silence.flac", Path.Combine(Downloads, "three.flac"));
        var registry = Registry(Options(StorageMode.Folders));

        await using (var db = NewContext())
        {
            var songs = new[] { AddSong(db, first), AddSong(db, second) };
            await db.SaveChangesAsync();
            foreach (var song in songs)
                await registry.LinkAsync(db, song, (await registry.ReadAsync(song.SourcePath, default))!, default);
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext())
        {
            var song = AddSong(db, third);
            await db.SaveChangesAsync();
            await registry.LinkAsync(db, song, (await registry.ReadAsync(third, default))!, default);
            await db.SaveChangesAsync();
        }

        await using var check = NewContext();
        var asset = Assert.Single(await check.AudioAssets.ToListAsync());
        Assert.Equal(Sha256Of(first), asset.Sha256);
        Assert.Equal(Normalize(first), asset.StoragePath);
        Assert.Equal("flac", asset.Container);
        Assert.Equal(new FileInfo(first).Length, asset.SizeBytes);
        Assert.All(await check.Songs.IgnoreQueryFilters().ToListAsync(), s => Assert.Equal(asset.Id, s.AudioAssetId));
    }

    [Fact]
    public async Task A_hosted_download_lands_in_the_content_store_builds_without_a_copy_and_plays()
    {
        var options = Options(StorageMode.Hosted);
        string? requestedDirectory = null;
        var provider = new CopyingProvider("silence.opus", req => requestedDirectory = req.DestinationDirectory);

        await using (var db = NewContext())
        {
            db.WishlistItems.Add(Pending("track-1"));
            await db.SaveChangesAsync();
            var (_, downloaded) = await Processor(provider, options).ProcessBatchAsync(db, Owner, default);
            Assert.Equal(1, downloaded);
        }

        Assert.Equal(WishlistDownloadProcessor.HostedStagingDirectory(options.Value), requestedDirectory);
        Assert.Empty(Directory.EnumerateFiles(requestedDirectory!));

        string stored;
        await using (var db = NewContext())
        {
            var item = await db.WishlistItems.IgnoreQueryFilters().SingleAsync();
            var asset = await db.AudioAssets.SingleAsync();
            Assert.Equal(WishlistItemStatus.Downloaded, item.Status);
            stored = item.DownloadedFilePath!;
            Assert.Equal(
                Normalize(Path.Combine(Downloads, "content", asset.Sha256[..2], asset.Sha256[2..4], $"{asset.Sha256}.opus")),
                stored);
            Assert.Equal(stored, asset.StoragePath);
            Assert.Equal(asset.Sha256, Sha256Of(stored));

            // What the scanner does next: a song at the stored path, which the linker finds by path.
            var song = AddSong(db, stored);
            song.EnrichmentStatus = EnrichmentStatus.Matched;
            await db.SaveChangesAsync();
            Assert.Equal(1, await Processor(provider, options).LinkDownloadedItemsAsync(db, Owner, default));
        }

        var before = File.ReadAllBytes(stored);
        var result = await HostedBuilder(options).ProcessNextBatchAsync(Guid.NewGuid());
        Assert.Equal(1, result.Done);

        await using var final = NewContext();
        var built = await final.Songs.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(LibraryBuildStatus.Done, built.LibraryBuildStatus);
        Assert.Null(built.DestinationPath);
        Assert.Equal((await final.AudioAssets.SingleAsync()).Id, built.AudioAssetId);
        Assert.True(built.IsBuilt);
        Assert.Equal(before, File.ReadAllBytes(stored));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Destination));
        Assert.Equal(stored, SongsEndpoints.ResolveAudioFilePath(built));
        Assert.True(IsBuiltInSongsList(await ListSongsCaller.Invoke(NewContextFor(TestCurrentUserAccessor.OwnerUser), Downloads)));
    }

    [Fact]
    public async Task A_second_download_of_the_same_bytes_reuses_the_stored_file()
    {
        var options = Options(StorageMode.Hosted);
        var provider = new CopyingProvider("silence.opus");
        await using (var db = NewContext())
        {
            db.WishlistItems.AddRange(Pending("track-1"), Pending("track-2"));
            await db.SaveChangesAsync();
            await Processor(provider, options).ProcessBatchAsync(db, Owner, default);
        }
        await using (var db = NewContext())
        {
            db.WishlistItems.Add(Pending("track-3"));
            await db.SaveChangesAsync();
            await Processor(provider, options).ProcessBatchAsync(db, Owner, default);
        }

        await using var check = NewContext();
        var asset = Assert.Single(await check.AudioAssets.ToListAsync());
        var items = await check.WishlistItems.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(3, items.Count);
        Assert.All(items, i => Assert.Equal(asset.StoragePath, i.DownloadedFilePath));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(Downloads, "content"), "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(WishlistDownloadProcessor.HostedStagingDirectory(options.Value)));
    }

    [Fact]
    public async Task A_hosted_build_registers_the_file_in_place_and_never_copies_or_tags_it()
    {
        var path = CopyFixture("silence.flac", Path.Combine(Source, "Artist", "Album", "01.flac"));
        var lastWrite = File.GetLastWriteTimeUtc(path);
        var sha = Sha256Of(path);
        await using (var db = NewContext())
        {
            var song = AddSong(db, path);
            song.EnrichmentStatus = EnrichmentStatus.Matched;
            song.Title = "A title the file does not carry";
            var missing = AddSong(db, Path.Combine(Source, "gone.flac"));
            missing.EnrichmentStatus = EnrichmentStatus.Matched;
            await db.SaveChangesAsync();
        }

        var result = await HostedBuilder(Options(StorageMode.Hosted)).ProcessNextBatchAsync(Guid.NewGuid());

        Assert.Equal((1, 1), (result.Done, result.Failed));
        await using var check = NewContext();
        var built = await check.Songs.IgnoreQueryFilters().SingleAsync(s => s.SourcePath == Normalize(path));
        var asset = await check.AudioAssets.SingleAsync();
        Assert.Equal(LibraryBuildStatus.Done, built.LibraryBuildStatus);
        Assert.Equal(asset.Id, built.AudioAssetId);
        Assert.Equal(Normalize(path), asset.StoragePath);
        Assert.Null(built.DestinationPath);
        Assert.True(built.IsBuilt);
        Assert.Equal(sha, Sha256Of(path));
        Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Destination));

        var failed = await check.Songs.IgnoreQueryFilters().SingleAsync(s => s.Id != built.Id);
        Assert.Equal(LibraryBuildStatus.Failed, failed.LibraryBuildStatus);
        Assert.Equal(HostedLibraryBuilder.MissingFileError, failed.LibraryBuildError);
        Assert.Null(failed.AudioAssetId);

        // Built and linked, it is no longer a candidate: the poll and the build agree it is done.
        Assert.Equal(0, await HostedCandidates(check).CountAsync(s => s.Id == built.Id));
    }

    [Fact]
    public async Task Adoption_links_settled_originals_where_they_are_and_reads_nothing_else()
    {
        var original = CopyFixture("silence.flac", Path.Combine(Source, "old.flac"), settled: true);
        var download = CopyFixture("silence.mp3", Path.Combine(Downloads, "dl.mp3"), settled: true);
        var fresh = CopyFixture("silence.opus", Path.Combine(Source, "fresh.opus"));
        var tagged = CopyFixture("silence.m4a", Path.Combine(Destination, "built.m4a"), settled: true);
        var released = Path.Combine(Downloads, "released.flac");
        var stamps = new[] { original, download }.ToDictionary(p => p, p => (Sha256Of(p), File.GetLastWriteTimeUtc(p)));

        await using (var db = NewContext())
        {
            AddSong(db, original).DestinationPath = Path.Combine(Destination, "copy.flac");
            AddSong(db, download);
            AddSong(db, fresh);
            AddSong(db, tagged);
            AddSong(db, released).SourceReleasedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        var options = Options(StorageMode.Folders, adoption: true);
        AudioAssetAdoptionPage page;
        await using (var db = NewContext())
            page = (await Adoption(db, options).AdoptNextPageAsync(0, default))!;

        Assert.Equal((3, 2), (page.Examined, page.Linked));
        await using var check = NewContext();
        var songs = await check.Songs.IgnoreQueryFilters().ToDictionaryAsync(s => s.SourcePath);
        Assert.NotNull(songs[Normalize(original)].AudioAssetId);
        Assert.NotNull(songs[Normalize(download)].AudioAssetId);
        Assert.Null(songs[Normalize(fresh)].AudioAssetId);
        Assert.Null(songs[Normalize(tagged)].AudioAssetId);
        Assert.Null(songs[Normalize(released)].AudioAssetId);
        Assert.Equal(
            [Normalize(download), Normalize(original)],
            (await check.AudioAssets.Select(a => a.StoragePath).ToListAsync()).Order());
        foreach (var (path, (sha, written)) in stamps)
            Assert.Equal((sha, written), (Sha256Of(path), File.GetLastWriteTimeUtc(path)));
        Assert.False(Directory.Exists(Path.Combine(Downloads, "content")));

        // The cursor moves past what it examined; a restart picks up only what is still unlinked.
        await using (var db = NewContext())
            Assert.Null(await Adoption(db, options).AdoptNextPageAsync(page.LastSongId, default));
        await using (var db = NewContext())
            Assert.Equal(1, await db.Songs.IgnoreQueryFilters().WhereAdoptable(options.Value, db.UpgradeRequests).CountAsync());
    }

    [Fact]
    public async Task Folder_mode_downloads_into_the_download_folder_and_registers_nothing()
    {
        var options = Options(StorageMode.Folders);
        string? requestedDirectory = null;
        var provider = new CopyingProvider("silence.opus", req => requestedDirectory = req.DestinationDirectory);
        await using (var db = NewContext())
        {
            db.WishlistItems.Add(Pending("track-1"));
            await db.SaveChangesAsync();
            await Processor(provider, options).ProcessBatchAsync(db, Owner, default);
        }

        Assert.Equal(Downloads, requestedDirectory);
        await using var check = NewContext();
        var item = await check.WishlistItems.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(Normalize(provider.Written.Single()), item.DownloadedFilePath);
        Assert.True(File.Exists(item.DownloadedFilePath));
        Assert.Empty(await check.AudioAssets.ToListAsync());
        Assert.False(Directory.Exists(Path.Combine(Downloads, "content")));
    }

    [Fact]
    public async Task Folder_mode_still_builds_a_done_song_that_has_an_asset_but_no_destination_copy()
    {
        await using var db = NewContext();
        var song = AddSong(db, Path.Combine(Source, "x.flac"));
        song.EnrichmentStatus = EnrichmentStatus.Matched;
        song.LibraryBuildStatus = LibraryBuildStatus.Done;
        song.AudioAsset = new AudioAsset { Sha256 = new string('a', 64), StoragePath = song.SourcePath, SizeBytes = 1, Container = "flac" };
        await db.SaveChangesAsync();

        Assert.Equal(1, await LibraryBuildQuery.BuildCandidates(db.Songs.IgnoreQueryFilters(), null, 5).CountAsync());
        Assert.Equal(0, await HostedCandidates(db).CountAsync());
        Assert.Null(LibraryBuildQuery.LyricsWaitCutoff(Options(StorageMode.Hosted).Value));
        Assert.NotNull(LibraryBuildQuery.LyricsWaitCutoff(Options(StorageMode.Folders).Value));
    }

    [Theory]
    [InlineData(StorageMode.Folders, "/elsewhere/content", true)]
    [InlineData(StorageMode.Hosted, "", true)]
    [InlineData(StorageMode.Hosted, "/data/downloads/content", true)]
    [InlineData(StorageMode.Hosted, "/elsewhere/content", false)]
    [InlineData(StorageMode.Hosted, "/data/downloads", false)]
    [InlineData(StorageMode.Hosted, "/data/downloads/.store", false)]
    [InlineData(StorageMode.Hosted, "/data/downloads/videos/content", false)]
    public void Hosted_mode_requires_the_content_store_under_the_download_folder(StorageMode mode, string store, bool valid)
    {
        var result = new MusicEnricherOptionsValidator().Validate(null, new MusicEnricherOptions
        {
            SourceDirectory = "/music",
            DestinationDirectory = "/library",
            DownloadDirectory = "/data/downloads",
            StorageMode = mode,
            ContentStoreDirectory = store,
        });
        Assert.Equal(valid, result.Succeeded);
    }

    [Fact]
    public async Task The_release_sweep_never_runs_in_hosted_mode_or_frees_an_assets_file()
    {
        var hosted = Options(StorageMode.Hosted);
        hosted.Value.EnableWishlistDownloads = true;
        await using var db = NewContext();
        Assert.Equal(StagedSourceReleaseResult.IdleHostedStorage, ReleaseService(db, hosted).UnavailableReason());

        var folders = Options(StorageMode.Folders);
        folders.Value.EnableWishlistDownloads = true;
        var song = AddSong(db, Path.Combine(Downloads, "a.flac"));
        song.LibraryBuildStatus = LibraryBuildStatus.Done;
        song.DestinationPath = Path.Combine(Destination, "a.flac");
        song.LibraryBuiltAtUtc = DateTime.UtcNow.AddDays(-1);
        await db.SaveChangesAsync();
        Assert.Equal(1, (await ReleaseService(db, folders).PreviewAsync(Owner)).Eligible);

        song.AudioAsset = new AudioAsset { Sha256 = new string('b', 64), StoragePath = song.SourcePath, SizeBytes = 1, Container = "flac" };
        await db.SaveChangesAsync();
        Assert.Equal(0, (await ReleaseService(db, folders).PreviewAsync(Owner)).Eligible);
    }

    [Fact]
    public async Task In_hosted_mode_new_lyrics_do_not_take_a_built_song_out_of_the_library()
    {
        foreach (var (mode, staysBuilt) in new[] { (StorageMode.Hosted, true), (StorageMode.Folders, false) })
        {
            var options = new MusicEnricherOptions { StorageMode = mode, AutoStartPipeline = true };
            var name = Guid.NewGuid().ToString("N");
            DbContextOptions<MusicHoarderDbContext> Context() => new DbContextOptionsBuilder<MusicHoarderDbContext>()
                .UseInMemoryDatabase(name)
                .AddInterceptors(new MusicHoarder.Api.Persistence.Interceptors.RebuildOnMetadataChangeInterceptor(new StaticOptionsMonitor<MusicEnricherOptions>(options)))
                .Options;
            await using (var db = new MusicHoarderDbContext(Context()))
            {
                var song = AddSong(db, Path.Combine(Source, "a.flac"));
                song.LibraryBuildStatus = LibraryBuildStatus.Done;
                song.DestinationPath = mode == StorageMode.Folders ? Path.Combine(Destination, "a.flac") : null;
                song.AudioAsset = new AudioAsset { Sha256 = new string('c', 64), StoragePath = song.SourcePath, SizeBytes = 1, Container = "flac" };
                await db.SaveChangesAsync();
            }
            await using (var db = new MusicHoarderDbContext(Context()))
            {
                var song = await db.Songs.IgnoreQueryFilters().SingleAsync();
                song.SyncedLyrics = "[00:01.00] words";
                await db.SaveChangesAsync();
                Assert.Equal(staysBuilt, song.IsBuilt);
            }
        }
    }

    [Fact]
    public async Task A_hosted_build_of_a_released_row_plays_its_old_library_copy()
    {
        var copy = CopyFixture("silence.flac", Path.Combine(Destination, "a.flac"));
        await using (var db = NewContext())
        {
            var song = AddSong(db, Path.Combine(Downloads, "a.flac"));
            song.EnrichmentStatus = EnrichmentStatus.Matched;
            song.SourceReleasedAtUtc = DateTime.UtcNow;
            song.PreviousDestinationPath = Normalize(copy);
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, (await HostedBuilder(Options(StorageMode.Hosted)).ProcessNextBatchAsync(Guid.NewGuid())).Done);

        await using var check = NewContext();
        var built = await check.Songs.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(Normalize(copy), built.DestinationPath);
        Assert.Null(built.PreviousDestinationPath);
        Assert.True(built.IsBuilt);
        Assert.Empty(await check.AudioAssets.ToListAsync());
    }

    [Fact]
    public async Task A_file_another_song_plays_is_not_purged_and_a_purged_asset_comes_back_when_its_bytes_do()
    {
        var path = CopyFixture("silence.flac", Path.Combine(Downloads, "shared.flac"));
        var registry = Registry(Options(StorageMode.Folders));
        await using var db = NewContext();
        var first = AddSong(db, path);
        var second = AddSong(db, Path.Combine(Source, "same-bytes.flac"));
        await db.SaveChangesAsync();
        var facts = (await registry.ReadAsync(path, default))!;
        var asset = await registry.LinkAsync(db, first, facts, default);
        await registry.LinkAsync(db, second, facts, default);
        await db.SaveChangesAsync();

        Assert.False(await registry.TryPurgeForSongAsync(db, path, first.Id, default));
        Assert.Null(asset.PurgedAtUtc);

        second.SoftDelete();
        await db.SaveChangesAsync();
        Assert.True(await registry.TryPurgeForSongAsync(db, path, first.Id, default));
        Assert.NotNull(asset.PurgedAtUtc);

        var third = AddSong(db, Path.Combine(Source, "back.flac"));
        await db.SaveChangesAsync();
        await registry.LinkAsync(db, third, facts with { Path = Normalize(Path.Combine(Source, "back.flac")) }, default);
        await db.SaveChangesAsync();
        Assert.Null(asset.PurgedAtUtc);
        Assert.Equal(Normalize(Path.Combine(Source, "back.flac")), asset.StoragePath);
        Assert.Single(await db.AudioAssets.ToListAsync());
    }

    [Fact]
    public async Task Adoption_leaves_a_quality_upgrade_download_that_waits_to_be_merged()
    {
        var pending = CopyFixture("silence.flac", Path.Combine(Downloads, "upgrade.flac"), settled: true);
        await using var db = NewContext();
        AddSong(db, pending);
        db.UpgradeRequests.Add(new UpgradeRequest
        {
            OwnerUserId = Owner,
            SongId = 999,
            Status = UpgradeRequestStatus.AwaitingIngest,
            DownloadedFilePath = Normalize(pending),
        });
        await db.SaveChangesAsync();

        var options = Options(StorageMode.Folders, adoption: true);
        Assert.Equal(0, await db.Songs.IgnoreQueryFilters().WhereAdoptable(options.Value, db.UpgradeRequests).CountAsync());
    }

    [Fact]
    public async Task Adoption_waits_for_the_scanner_when_a_file_changed_without_changing_size()
    {
        var path = CopyFixture("silence.flac", Path.Combine(Source, "retagged.flac"), settled: true);
        await using (var db = NewContext())
        {
            AddSong(db, path).LastModifiedUtc = File.GetLastWriteTimeUtc(path).AddMinutes(-5);
            await db.SaveChangesAsync();
        }

        var options = Options(StorageMode.Folders, adoption: true);
        await using (var db = NewContext())
            Assert.Equal(0, (await Adoption(db, options).AdoptNextPageAsync(0, default))!.Linked);
        await using var check = NewContext();
        Assert.Empty(await check.AudioAssets.ToListAsync());
    }

    [Fact]
    public async Task A_download_is_kept_when_the_in_place_file_with_its_hash_has_since_changed()
    {
        var options = Options(StorageMode.Hosted);
        var provider = new CopyingProvider("silence.opus");
        await using (var db = NewContext())
        {
            db.WishlistItems.Add(Pending("track-1"));
            await db.SaveChangesAsync();
            await Processor(provider, options).ProcessBatchAsync(db, Owner, default);
        }

        // The same bytes registered where they were, then changed by something outside the app.
        var inPlace = Normalize(Path.Combine(Source, "original.opus"));
        await using (var db = NewContext())
        {
            var asset = await db.AudioAssets.SingleAsync();
            File.Move(asset.StoragePath, inPlace);
            asset.StoragePath = inPlace;
            await db.SaveChangesAsync();
        }
        File.AppendAllText(inPlace, "changed outside the app");

        await using (var db = NewContext())
        {
            db.WishlistItems.Add(Pending("track-2"));
            await db.SaveChangesAsync();
            await Processor(provider, options).ProcessBatchAsync(db, Owner, default);
        }

        await using var check = NewContext();
        var stored = await check.AudioAssets.SingleAsync();
        var item = await check.WishlistItems.IgnoreQueryFilters().SingleAsync(i => i.SpotifyTrackId == "track-2");
        Assert.StartsWith(Normalize(Path.Combine(Downloads, "content")), stored.StoragePath);
        Assert.Equal(stored.StoragePath, item.DownloadedFilePath);
        Assert.Equal(stored.Sha256, Sha256Of(stored.StoragePath));
    }

    [Fact]
    public async Task A_hosted_build_fails_an_unreadable_file_and_builds_the_rest()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
            return;
        var locked = CopyFixture("silence.flac", Path.Combine(Source, "locked.flac"));
        var open = CopyFixture("silence.mp3", Path.Combine(Source, "open.mp3"));
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            await using (var db = NewContext())
            {
                AddSong(db, locked).EnrichmentStatus = EnrichmentStatus.Matched;
                AddSong(db, open).EnrichmentStatus = EnrichmentStatus.Matched;
                await db.SaveChangesAsync();
            }

            var result = await HostedBuilder(Options(StorageMode.Hosted)).ProcessNextBatchAsync(Guid.NewGuid());

            Assert.Equal((1, 1), (result.Done, result.Failed));
            await using var check = NewContext();
            var failed = await check.Songs.IgnoreQueryFilters().SingleAsync(s => s.SourcePath == Normalize(locked));
            Assert.Equal(HostedLibraryBuilder.UnreadableFileError, failed.LibraryBuildError);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [PostgresFact]
    public async Task On_postgres_one_hash_is_one_row_and_the_hosted_queries_translate()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var path = CopyFixture("silence.flac", Path.Combine(Source, "pg.flac"), settled: true);
        var options = Options(StorageMode.Hosted, adoption: true);
        var registry = Registry(options);

        await using (var db = database.NewContext())
        {
            var song = AddSong(db, path);
            song.EnrichmentStatus = EnrichmentStatus.Matched;
            await db.SaveChangesAsync();
            Assert.Equal(1, await db.Songs.IgnoreQueryFilters().WhereAdoptable(options.Value, db.UpgradeRequests).CountAsync());
            Assert.Equal(1, await HostedCandidates(db).CountAsync());

            await registry.LinkAsync(db, song, (await registry.ReadAsync(path, default))!, default);
            song.MarkBuiltInPlace();
            await db.SaveChangesAsync();
            Assert.Equal(0, await db.Songs.IgnoreQueryFilters().WhereAdoptable(options.Value, db.UpgradeRequests).CountAsync());
            Assert.Equal(0, await HostedCandidates(db).CountAsync());
        }

        await using (var db = database.NewContext())
        {
            db.AudioAssets.Add(new AudioAsset { Sha256 = Sha256Of(path), StoragePath = "/other", SizeBytes = 1, Container = "flac" });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        await using (var db = database.NewContextFor(TestCurrentUserAccessor.OwnerUser))
            Assert.True(IsBuiltInSongsList(await ListSongsCaller.Invoke(db)));
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private IOptions<MusicEnricherOptions> Options(StorageMode mode, bool adoption = false)
        => Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = Source,
            DestinationDirectory = Destination,
            DownloadDirectory = Downloads,
            DownloadProvider = "fake",
            StorageMode = mode,
            EnableAudioAssetAdoption = adoption,
            ScanSettleSeconds = 60,
            LyricsBeforeBuildWaitMinutes = 10,
        });

    private static AudioAssetRegistry Registry(IOptions<MusicEnricherOptions> options)
        => new(options, TimeProvider.System, NullLogger<AudioAssetRegistry>.Instance);

    private WishlistDownloadProcessor Processor(IDownloadProvider provider, IOptions<MusicEnricherOptions> options)
        => new(
            [provider],
            new DownloadProgressTracker(),
            new FakeMusicVideoDownloader(),
            new MusicVideoChannel(),
            new FakeDownloadArtworkEmbedder(),
            Registry(options),
            options,
            NullLogger<WishlistDownloadProcessor>.Instance);

    private HostedLibraryBuilder HostedBuilder(IOptions<MusicEnricherOptions> options)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => NewContext());
        return new HostedLibraryBuilder(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Registry(options),
            options,
            TestPipelineMetrics.Create(),
            NullLogger<HostedLibraryBuilder>.Instance);
    }

    private static AudioAssetAdoptionService Adoption(MusicHoarderDbContext db, IOptions<MusicEnricherOptions> options)
        => new(db, Registry(options), options, TimeProvider.System, NullLogger<AudioAssetAdoptionService>.Instance);

    private static StagedSourceReleaseService ReleaseService(MusicHoarderDbContext db, IOptions<MusicEnricherOptions> options)
        => new(
            db,
            new System.IO.Abstractions.FileSystem(),
            new TagLibAudioFileProbe(NullLogger<TagLibAudioFileProbe>.Instance),
            options,
            new StaticOptionsMonitor<SyncOptions>(new SyncOptions()),
            new StagedSourceReleaseTracker(),
            NullLogger<StagedSourceReleaseService>.Instance);

    private static IQueryable<SongMetadata> HostedCandidates(MusicHoarderDbContext db)
        => LibraryBuildQuery.BuildCandidates(db.Songs.IgnoreQueryFilters(), null, 5, hostedStorage: true);

    private MusicHoarderDbContext NewContext()
        => new(new DbContextOptionsBuilder<MusicHoarderDbContext>().UseInMemoryDatabase(_databaseName).Options);

    private MusicHoarderDbContext NewContextFor(CurrentUser caller)
        => new(
            new DbContextOptionsBuilder<MusicHoarderDbContext>().UseInMemoryDatabase(_databaseName).Options,
            new TestCurrentUserAccessor(caller));

    private static SongMetadata AddSong(MusicHoarderDbContext db, string path)
    {
        var normalized = Normalize(path);
        var song = new SongMetadata
        {
            OwnerUserId = Owner,
            SourcePath = normalized,
            FileName = Path.GetFileName(normalized),
            Extension = Path.GetExtension(normalized),
            FileSizeBytes = File.Exists(path) ? new FileInfo(path).Length : 1,
            LastModifiedUtc = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Title = "Title",
            Artist = "Artist",
            Album = "Album",
        };
        db.Songs.Add(song);
        return song;
    }

    private static WishlistItem Pending(string trackId) => new()
    {
        OwnerUserId = Owner,
        SpotifyTrackId = trackId,
        Title = "Title",
        Artist = "Artist",
        Album = "Album",
        DurationMs = 1_000,
        Status = WishlistItemStatus.Pending,
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow,
    };

    private static string CopyFixture(string fixture, string target, bool settled = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(Path.Combine(FixtureDir, fixture), target, overwrite: true);
        File.SetLastWriteTimeUtc(target, settled ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow);
        return target;
    }

    private static bool IsBuiltInSongsList(Microsoft.AspNetCore.Http.IResult result)
    {
        var value = result.GetType().GetProperty("Value")!.GetValue(result)!;
        var row = Assert.Single(((IEnumerable)value.GetType().GetProperty("Songs")!.GetValue(value)!).Cast<object>());
        return (bool)row.GetType().GetProperty("IsBuilt")!.GetValue(row)!;
    }

    private static string Sha256Of(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static string Normalize(string path) => path.Replace('\\', '/');

    /// <summary>Writes a fixture into whatever folder the processor asks for, as a real provider would.</summary>
    private sealed class CopyingProvider(string fixture, Action<DownloadRequest>? onRequest = null) : IDownloadProvider
    {
        public List<string> Written { get; } = [];
        public string Name => "fake";

        public Task<DownloadResult> DownloadAsync(DownloadRequest req, CancellationToken ct)
        {
            onRequest?.Invoke(req);
            var target = Path.Combine(req.DestinationDirectory, $"{Guid.NewGuid():N}{Path.GetExtension(fixture)}");
            File.Copy(Path.Combine(FixtureDir, fixture), target);
            lock (Written) Written.Add(target);
            return Task.FromResult(DownloadResult.Ok(target));
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
