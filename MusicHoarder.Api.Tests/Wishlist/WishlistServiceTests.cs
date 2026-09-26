using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Import;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;
using MusicHoarder.Api.Tests.Deezer;
using MusicHoarder.Api.Tests.Import;
using MusicHoarder.Api.Wishlist;

namespace MusicHoarder.Api.Tests.Wishlist;

public class WishlistServiceTests
{
    private static readonly Guid Owner = WellKnownUsers.OwnerId;

    [Fact]
    public async Task AddSource_LikedSongs_CreatesSourceAndSnapshotsTracks()
    {
        await using var db = CreateDbContext();
        var api = new FakeSpotifyApi
        {
            LikedSongs = [Track("a", "Song A"), Track("b", "Song B")],
        };
        var service = CreateService(db, api);

        var result = await service.AddSourceAsync(Owner, WishlistSourceType.LikedSongs, null, autoSync: true, default);

        Assert.Equal(2, result.Added);
        var source = await db.WishlistSources.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(WishlistSourceType.LikedSongs, source.SourceType);
        Assert.Null(source.SpotifyPlaylistId);
        Assert.True(source.AutoSync);
        Assert.NotNull(source.LastSyncedAtUtc);
        Assert.Equal(2, await db.WishlistItems.IgnoreQueryFilters().CountAsync());
        Assert.All(await db.WishlistItems.IgnoreQueryFilters().ToListAsync(),
            i => Assert.Equal(WishlistItemStatus.Pending, i.Status));
    }

    [Fact]
    public async Task AddSource_Playlist_RequiresPlaylistId()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, new FakeSpotifyApi());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AddSourceAsync(Owner, WishlistSourceType.Playlist, null, false, default));
    }

    [Fact]
    public async Task SyncSource_DedupesAgainstExistingWishlistItems()
    {
        await using var db = CreateDbContext();
        db.WishlistItems.Add(new WishlistItem
        {
            OwnerUserId = Owner,
            SpotifyTrackId = "a",
            Title = "Song A",
            Artist = "Artist",
            Status = WishlistItemStatus.Downloaded, // already acquired
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        var source = new WishlistSource
        {
            OwnerUserId = Owner,
            SourceType = WishlistSourceType.LikedSongs,
            Name = "Liked Songs",
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.WishlistSources.Add(source);
        await db.SaveChangesAsync();

        var api = new FakeSpotifyApi { LikedSongs = [Track("a", "Song A"), Track("c", "Song C")] };
        var service = CreateService(db, api);

        var result = await service.SyncSourceAsync(Owner, source, default);

        Assert.Equal(1, result.Added);          // only "c" is new
        Assert.Equal(1, result.AlreadyPresent);  // "a" already on the wishlist
        Assert.Equal(2, await db.WishlistItems.IgnoreQueryFilters().CountAsync());
        // The pre-existing acquired item is untouched.
        var existing = await db.WishlistItems.IgnoreQueryFilters().FirstAsync(i => i.SpotifyTrackId == "a");
        Assert.Equal(WishlistItemStatus.Downloaded, existing.Status);
    }

    [Fact]
    public async Task SyncSource_DedupesDuplicatesWithinSameFetch()
    {
        await using var db = CreateDbContext();
        var source = new WishlistSource
        {
            OwnerUserId = Owner,
            SourceType = WishlistSourceType.LikedSongs,
            Name = "Liked Songs",
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.WishlistSources.Add(source);
        await db.SaveChangesAsync();

        var api = new FakeSpotifyApi { LikedSongs = [Track("a", "Song A"), Track("a", "Song A again")] };
        var service = CreateService(db, api);

        var result = await service.SyncSourceAsync(Owner, source, default);

        Assert.Equal(1, result.Added);
        Assert.Equal(1, await db.WishlistItems.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task SyncSource_CarriesIsrcOntoItems()
    {
        await using var db = CreateDbContext();
        var source = new WishlistSource
        {
            OwnerUserId = Owner,
            SourceType = WishlistSourceType.LikedSongs,
            Name = "Liked Songs",
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.WishlistSources.Add(source);
        await db.SaveChangesAsync();

        var api = new FakeSpotifyApi { LikedSongs = [Track("a", "Song A", isrc: "USRC12345678")] };
        var service = CreateService(db, api);

        await service.SyncSourceAsync(Owner, source, default);

        var item = await db.WishlistItems.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("USRC12345678", item.Isrc);
    }

    [Fact]
    public async Task SyncSource_FastPoll_StopsAfterMaxPages()
    {
        await using var db = CreateDbContext();
        var source = new WishlistSource
        {
            OwnerUserId = Owner,
            SourceType = WishlistSourceType.LikedSongs,
            Name = "Liked Songs",
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.WishlistSources.Add(source);
        await db.SaveChangesAsync();

        // 150 liked songs = 3 Spotify pages of 50; a 2-page fast poll must take only the newest 100.
        var liked = Enumerable.Range(0, 150).Select(i => Track($"t{i}", $"Song {i}")).ToList();
        var api = new FakeSpotifyApi { LikedSongs = liked };
        var service = CreateService(db, api);

        var result = await service.SyncSourceAsync(Owner, source, default, maxPages: 2);

        Assert.Equal(100, result.Added);
        Assert.Equal(100, await db.WishlistItems.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task SyncSource_DeezerPlaylist_InsertsPendingItemsWithMetadataAndResolvedSpotifyId()
    {
        await using var db = CreateDbContext();
        var source = new WishlistSource
        {
            OwnerUserId = Owner,
            SourceType = WishlistSourceType.DeezerPlaylist,
            DeezerPlaylistId = "pl1",
            Name = "RapCaviar",
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.WishlistSources.Add(source);
        await db.SaveChangesAsync();

        var deezer = new FakeDeezerCatalogService();
        deezer.Playlists["pl1"] = new DeezerPlaylistSummary("pl1", "RapCaviar", "hot", "cover", 2, "Deezer", "chk-1");
        deezer.PlaylistTracks["pl1"] =
        [
            new DeezerPlaylistTrack("d1", "Song One", "Artist A", "Album A", 200_000, "cov1"),
            new DeezerPlaylistTrack("d2", "Song Two", "Artist B", "Album B", 180_000, "cov2"),
        ];
        deezer.TracksById["d1"] = DeezerDetail("d1", "USAA11111111");
        deezer.TracksById["d2"] = DeezerDetail("d2", "USBB22222222");
        var resolver = new FakeSpotifyIsrcResolver();
        resolver.ByIsrc["USAA11111111"] = "sp-1"; // d1 resolves to a Spotify id; d2 does not.

        var service = CreateService(db, new FakeSpotifyApi(), deezer, resolver);
        var result = await service.SyncSourceAsync(Owner, source, default);

        Assert.Equal(2, result.Added);
        var items = await db.WishlistItems.IgnoreQueryFilters().OrderBy(i => i.DeezerTrackId).ToListAsync();
        Assert.Equal(2, items.Count);

        Assert.Equal("d1", items[0].DeezerTrackId);
        Assert.Equal("sp-1", items[0].SpotifyTrackId);
        Assert.Equal("Song One", items[0].Title);
        Assert.Equal("Artist A", items[0].Artist);
        Assert.Equal("Album A", items[0].Album);
        Assert.Equal("USAA11111111", items[0].Isrc);
        Assert.Equal(200_000, items[0].DurationMs);
        Assert.Equal("cov1", items[0].AlbumArt);
        Assert.Equal(WishlistItemStatus.Pending, items[0].Status);

        Assert.Equal("d2", items[1].DeezerTrackId);
        Assert.Null(items[1].SpotifyTrackId); // no Spotify match → null id, item still inserted

        // The stored checksum lets the next sync skip an unchanged playlist.
        var reloaded = await db.WishlistSources.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("chk-1", reloaded.RemoteChecksum);
        Assert.NotNull(reloaded.LastSyncedAtUtc);
    }

    [Fact]
    public async Task SyncSource_DeezerPlaylist_UnchangedChecksum_SkipsPaging()
    {
        await using var db = CreateDbContext();
        var source = new WishlistSource
        {
            OwnerUserId = Owner,
            SourceType = WishlistSourceType.DeezerPlaylist,
            DeezerPlaylistId = "pl1",
            Name = "RapCaviar",
            RemoteChecksum = "chk-1",
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.WishlistSources.Add(source);
        await db.SaveChangesAsync();

        var deezer = new FakeDeezerCatalogService();
        deezer.Playlists["pl1"] = new DeezerPlaylistSummary("pl1", "RapCaviar", null, null, 1, null, "chk-1");
        deezer.PlaylistTracks["pl1"] = [new DeezerPlaylistTrack("d1", "Song One", "Artist A", null, 200_000, null)];

        var service = CreateService(db, new FakeSpotifyApi(), deezer);
        var result = await service.SyncSourceAsync(Owner, source, default);

        Assert.Equal(0, result.Added);
        Assert.Equal(0, await db.WishlistItems.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task SyncSource_DeezerPlaylist_DedupesByDeezerTrackId()
    {
        await using var db = CreateDbContext();
        db.WishlistItems.Add(new WishlistItem
        {
            OwnerUserId = Owner,
            DeezerTrackId = "d1",
            Title = "Song One",
            Artist = "Artist A",
            Status = WishlistItemStatus.Downloaded,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        var source = new WishlistSource
        {
            OwnerUserId = Owner,
            SourceType = WishlistSourceType.DeezerPlaylist,
            DeezerPlaylistId = "pl1",
            Name = "RapCaviar",
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.WishlistSources.Add(source);
        await db.SaveChangesAsync();

        var deezer = new FakeDeezerCatalogService();
        deezer.Playlists["pl1"] = new DeezerPlaylistSummary("pl1", "RapCaviar", null, null, 2, null, "chk-2");
        deezer.PlaylistTracks["pl1"] =
        [
            new DeezerPlaylistTrack("d1", "Song One", "Artist A", null, 200_000, null),
            new DeezerPlaylistTrack("d2", "Song Two", "Artist B", null, 180_000, null),
        ];
        deezer.TracksById["d2"] = DeezerDetail("d2", null);

        var service = CreateService(db, new FakeSpotifyApi(), deezer);
        var result = await service.SyncSourceAsync(Owner, source, default);

        Assert.Equal(1, result.Added);          // only "d2" is new
        Assert.Equal(1, result.AlreadyPresent);  // "d1" already present
        Assert.Equal(2, await db.WishlistItems.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task SyncSource_DeezerPlaylist_DedupesByResolvedSpotifyId()
    {
        await using var db = CreateDbContext();
        // A Spotify-sourced item already on the wishlist under id "sp-1".
        db.WishlistItems.Add(new WishlistItem
        {
            OwnerUserId = Owner,
            SpotifyTrackId = "sp-1",
            Title = "Song One",
            Artist = "Artist A",
            Status = WishlistItemStatus.Downloaded,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        var source = new WishlistSource
        {
            OwnerUserId = Owner,
            SourceType = WishlistSourceType.DeezerPlaylist,
            DeezerPlaylistId = "pl1",
            Name = "RapCaviar",
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.WishlistSources.Add(source);
        await db.SaveChangesAsync();

        var deezer = new FakeDeezerCatalogService();
        deezer.Playlists["pl1"] = new DeezerPlaylistSummary("pl1", "RapCaviar", null, null, 1, null, "chk-3");
        deezer.PlaylistTracks["pl1"] = [new DeezerPlaylistTrack("d1", "Song One", "Artist A", null, 200_000, null)];
        deezer.TracksById["d1"] = DeezerDetail("d1", "USAA11111111");
        var resolver = new FakeSpotifyIsrcResolver();
        resolver.ByIsrc["USAA11111111"] = "sp-1"; // resolves to the id already present

        var service = CreateService(db, new FakeSpotifyApi(), deezer, resolver);
        var result = await service.SyncSourceAsync(Owner, source, default);

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.AlreadyPresent);
        Assert.Equal(1, await db.WishlistItems.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task SyncSource_DeezerPlaylist_IncompleteFetch_InsertsItemsButLeavesChecksumUnsetSoNextSyncRetries()
    {
        await using var db = CreateDbContext();
        var source = new WishlistSource
        {
            OwnerUserId = Owner,
            SourceType = WishlistSourceType.DeezerPlaylist,
            DeezerPlaylistId = "pl1",
            Name = "RapCaviar",
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.WishlistSources.Add(source);
        await db.SaveChangesAsync();

        // A page fetch failed mid-run: only the first track was paged and the fetch is flagged incomplete.
        var deezer = new FakeDeezerCatalogService();
        deezer.Playlists["pl1"] = new DeezerPlaylistSummary("pl1", "RapCaviar", null, null, 2, null, "chk-1");
        deezer.PlaylistTracks["pl1"] = [new DeezerPlaylistTrack("d1", "Song One", "Artist A", null, 200_000, null)];
        deezer.TracksById["d1"] = DeezerDetail("d1", null);
        deezer.IncompletePlaylistTracks.Add("pl1");

        var service = CreateService(db, new FakeSpotifyApi(), deezer);
        var first = await service.SyncSourceAsync(Owner, source, default);

        // The paged item is persisted, but the checksum is NOT advanced (an incomplete fetch must not
        // let the skip-if-unchanged path permanently hide the never-fetched tail).
        Assert.Equal(1, first.Added);
        Assert.Equal(1, await db.WishlistItems.IgnoreQueryFilters().CountAsync());
        var afterFirst = await db.WishlistSources.IgnoreQueryFilters().SingleAsync();
        Assert.Null(afterFirst.RemoteChecksum);
        Assert.NotNull(afterFirst.LastSyncedAtUtc);

        // Next sync: the fetch now completes with the full tracklist. Because the checksum was never
        // stored, the sync re-runs (rather than checksum-skipping) and picks up the missing track.
        deezer.PlaylistTracks["pl1"] =
        [
            new DeezerPlaylistTrack("d1", "Song One", "Artist A", null, 200_000, null),
            new DeezerPlaylistTrack("d2", "Song Two", "Artist B", null, 180_000, null),
        ];
        deezer.TracksById["d2"] = DeezerDetail("d2", null);
        deezer.IncompletePlaylistTracks.Clear();

        var second = await service.SyncSourceAsync(Owner, afterFirst, default);

        Assert.Equal(1, second.Added);          // the previously-missing "d2"
        Assert.Equal(1, second.AlreadyPresent);  // "d1" already present
        Assert.Equal(2, await db.WishlistItems.IgnoreQueryFilters().CountAsync());
        var afterSecond = await db.WishlistSources.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("chk-1", afterSecond.RemoteChecksum); // now advanced on the complete fetch
    }

    [Fact]
    public async Task CreateOrUpdateSource_DeezerPlaylist_ResolvesNameAndCoverFromDeezer()
    {
        await using var db = CreateDbContext();
        var deezer = new FakeDeezerCatalogService();
        deezer.Playlists["pl1"] = new DeezerPlaylistSummary("pl1", "RapCaviar", "desc", "cover-url", 50, "Deezer", "chk");

        var service = CreateService(db, new FakeSpotifyApi(), deezer);
        var source = await service.CreateOrUpdateSourceAsync(Owner, WishlistSourceType.DeezerPlaylist, "pl1", autoSync: true, default);

        Assert.Equal("RapCaviar", source.Name);
        Assert.Equal("cover-url", source.ImageUrl);
        Assert.Equal("pl1", source.DeezerPlaylistId);
        Assert.Null(source.SpotifyPlaylistId);
    }

    [Fact]
    public async Task CreateOrUpdateSource_DeezerPlaylist_RequiresDeezerPlaylistId()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, new FakeSpotifyApi());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateOrUpdateSourceAsync(Owner, WishlistSourceType.DeezerPlaylist, null, false, default));
    }

    // ── YouTube playlists ────────────────────────────────────────────────────────

    private const string YouTubeListId = "PLexampleCoolMusic";

    [Fact]
    public async Task SyncSource_YouTubePlaylist_QueuesEachVideoWithItsAudioAndMusicVideo()
    {
        await using var db = CreateDbContext();
        var source = await AddYouTubeSourceAsync(db);
        var reader = new FakeYouTubePlaylistReader();
        reader.Playlists[YouTubeListId] = FakeYouTubePlaylistReader.Playlist(YouTubeListId, "Cool music",
            new YouTubePlaylistEntry("ZJzr2Dsputk", "Kendrick Lamar - Alright (Official Music Video)", 415_000));
        var videos = new FakeYouTubeMetadataResolver();
        videos.Videos["https://www.youtube.com/watch?v=ZJzr2Dsputk"] = new YouTubeProbeResult(
            "Alright (Official Music Video)", "Kendrick Lamar", "To Pimp a Butterfly", 412_000,
            "https://i.ytimg.com/vi/ZJzr2Dsputk/maxresdefault.jpg");
        var service = CreateService(db, new FakeSpotifyApi(), youTubePlaylists: reader, youTubeVideos: videos);

        var result = await service.SyncSourceAsync(Owner, source, default);

        Assert.Equal(1, result.Added);
        var item = await db.WishlistItems.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(source.Id, item.WishlistSourceId);
        Assert.Equal("ZJzr2Dsputk", item.YouTubeVideoId);
        // The exact video, not a search: yt-dlp downloads this URL.
        Assert.Equal("https://www.youtube.com/watch?v=ZJzr2Dsputk", item.SourceUrl);
        Assert.Null(item.SpotifyTrackId);
        // The clip is why it is on the list, whatever the server's music-video default.
        Assert.True(item.DownloadMusicVideo);
        // Upload noise is stripped from the title the downloader stamps.
        Assert.Equal("Alright", item.Title);
        Assert.Equal("Kendrick Lamar", item.Artist);
        Assert.Equal("To Pimp a Butterfly", item.Album);
        Assert.Equal(412_000, item.DurationMs);
        Assert.Equal("https://i.ytimg.com/vi/ZJzr2Dsputk/maxresdefault.jpg", item.AlbumArt);
        Assert.Null(item.SpotifyAddedAtUtc);
        Assert.Equal(WishlistItemStatus.Pending, item.Status);
    }

    [Fact]
    public async Task SyncSource_YouTubePlaylist_ProbesOnlyNewVideosAndSkipsOnesAlreadyWishlisted()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        db.WishlistItems.AddRange(
            // Queued by an earlier sync of this playlist.
            new WishlistItem
            {
                OwnerUserId = Owner, YouTubeVideoId = "aaaaaaaaaaa", SourceUrl = "https://www.youtube.com/watch?v=aaaaaaaaaaa",
                Title = "A", Artist = "X", Status = WishlistItemStatus.Downloaded, CreatedAtUtc = now, UpdatedAtUtc = now,
            },
            // Pasted by hand before the video-id column existed: the id is only in the URL.
            new WishlistItem
            {
                OwnerUserId = Owner, SourceUrl = "https://www.youtube.com/watch?v=bbbbbbbbbbb",
                Title = "B", Artist = "X", Status = WishlistItemStatus.Downloaded, CreatedAtUtc = now, UpdatedAtUtc = now,
            });
        var source = await AddYouTubeSourceAsync(db);
        var reader = new FakeYouTubePlaylistReader();
        reader.Playlists[YouTubeListId] = FakeYouTubePlaylistReader.Playlist(YouTubeListId, "Cool music",
            new YouTubePlaylistEntry("aaaaaaaaaaa", "X - A", 200_000),
            new YouTubePlaylistEntry("bbbbbbbbbbb", "X - B", 200_000),
            new YouTubePlaylistEntry("ccccccccccc", "X - C", 200_000),
            new YouTubePlaylistEntry("ccccccccccc", "X - C", 200_000)); // listed twice
        var videos = new FakeYouTubeMetadataResolver();
        var service = CreateService(db, new FakeSpotifyApi(), youTubePlaylists: reader, youTubeVideos: videos);

        var result = await service.SyncSourceAsync(Owner, source, default);

        Assert.Equal(1, result.Added);
        Assert.Equal(3, result.AlreadyPresent);
        Assert.Equal(["https://www.youtube.com/watch?v=ccccccccccc"], videos.Probes);
        Assert.Equal(3, await db.WishlistItems.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task SyncSource_YouTubePlaylist_SkipsAVideoAnotherSyncStoredMeanwhile()
    {
        // A first snapshot is slow (one probe per video), so the periodic sweep can reach the same
        // playlist while it runs. Whatever the other run stores in the meantime is neither probed
        // again nor added twice.
        var dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var source = await AddYouTubeSourceAsync(db);
        var reader = new FakeYouTubePlaylistReader();
        reader.Playlists[YouTubeListId] = FakeYouTubePlaylistReader.Playlist(YouTubeListId, "Cool music",
            new YouTubePlaylistEntry("aaaaaaaaaaa", "X - A", 200_000),
            new YouTubePlaylistEntry("bbbbbbbbbbb", "X - B", 200_000));
        var videos = new FakeYouTubeMetadataResolver
        {
            // While this run probes A, the other run stores B.
            OnProbe = async url =>
            {
                if (!url.EndsWith("aaaaaaaaaaa", StringComparison.Ordinal)) return;
                await using var other = CreateDbContext(dbName);
                other.WishlistItems.Add(new WishlistItem
                {
                    OwnerUserId = Owner, WishlistSourceId = source.Id, YouTubeVideoId = "bbbbbbbbbbb",
                    SourceUrl = "https://www.youtube.com/watch?v=bbbbbbbbbbb", Title = "B", Artist = "X",
                    Status = WishlistItemStatus.Pending, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
                });
                await other.SaveChangesAsync();
            },
        };
        var service = CreateService(db, new FakeSpotifyApi(), youTubePlaylists: reader, youTubeVideos: videos);

        var result = await service.SyncSourceAsync(Owner, source, default);

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.AlreadyPresent);
        Assert.Equal(["https://www.youtube.com/watch?v=aaaaaaaaaaa"], videos.Probes);
        Assert.Equal(1, await db.WishlistItems.IgnoreQueryFilters().CountAsync(w => w.YouTubeVideoId == "bbbbbbbbbbb"));
    }

    [Fact]
    public async Task SyncSource_YouTubePlaylist_FallsBackToTheListedTitleWhenTheProbeFails()
    {
        await using var db = CreateDbContext();
        var source = await AddYouTubeSourceAsync(db);
        var reader = new FakeYouTubePlaylistReader();
        reader.Playlists[YouTubeListId] = FakeYouTubePlaylistReader.Playlist(YouTubeListId, "Cool music",
            new YouTubePlaylistEntry("ZJzr2Dsputk", "Kendrick Lamar - Alright [Official Video]", 415_000));
        // No probe answer: the resolver reports the video as unreadable.
        var service = CreateService(db, new FakeSpotifyApi(), youTubePlaylists: reader, youTubeVideos: new FakeYouTubeMetadataResolver());

        await service.SyncSourceAsync(Owner, source, default);

        var item = await db.WishlistItems.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("Kendrick Lamar", item.Artist);
        Assert.Equal("Alright", item.Title);
        Assert.Null(item.Album);
        Assert.Equal(415_000, item.DurationMs);
        Assert.Equal("https://i.ytimg.com/vi/ZJzr2Dsputk/hqdefault.jpg", item.AlbumArt);
    }

    [Fact]
    public async Task SyncSource_YouTubePlaylist_ThatCannotBeReadAddsNothingAndSaysWhy()
    {
        await using var db = CreateDbContext();
        var source = await AddYouTubeSourceAsync(db);
        var reader = new FakeYouTubePlaylistReader { FailWith = "That playlist does not exist, or it is private." };
        var service = CreateService(db, new FakeSpotifyApi(), youTubePlaylists: reader);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SyncSourceAsync(Owner, source, default));

        Assert.Contains("private", ex.Message);
        Assert.Empty(await db.WishlistItems.IgnoreQueryFilters().ToListAsync());
        Assert.Null(source.LastSyncedAtUtc);
    }

    [Fact]
    public async Task SyncSource_YouTubePlaylist_TakesTheNameYouTubeGivesIt()
    {
        await using var db = CreateDbContext();
        var source = await AddYouTubeSourceAsync(db, name: YouTubeListId); // created while YouTube was unreadable
        var reader = new FakeYouTubePlaylistReader();
        reader.Playlists[YouTubeListId] = FakeYouTubePlaylistReader.Playlist(YouTubeListId, "Cool music");
        var service = CreateService(db, new FakeSpotifyApi(), youTubePlaylists: reader);

        await service.SyncSourceAsync(Owner, source, default);

        Assert.Equal("Cool music", source.Name);
        Assert.Equal($"https://i.ytimg.com/pl/{YouTubeListId}.jpg", source.ImageUrl);
        Assert.NotNull(source.LastSyncedAtUtc);
    }

    [Fact]
    public async Task CreateOrUpdateSource_YouTubePlaylist_StoresTheListIdAndReadsOnlyThePreview()
    {
        await using var db = CreateDbContext();
        var reader = new FakeYouTubePlaylistReader();
        reader.Playlists[YouTubeListId] = FakeYouTubePlaylistReader.Playlist(YouTubeListId, "Cool music",
            new YouTubePlaylistEntry("aaaaaaaaaaa", "X - A", 200_000),
            new YouTubePlaylistEntry("bbbbbbbbbbb", "X - B", 200_000));
        var service = CreateService(db, new FakeSpotifyApi(), youTubePlaylists: reader);

        var source = await service.CreateOrUpdateSourceAsync(Owner, WishlistSourceType.YouTubePlaylist, YouTubeListId, autoSync: true, default);

        Assert.Equal(YouTubeListId, source.YouTubePlaylistId);
        Assert.Null(source.SpotifyPlaylistId);
        Assert.Null(source.DeezerPlaylistId);
        Assert.Equal("Cool music", source.Name);
        Assert.False(source.NeedsSpotify);
        // Naming the source reads one page, not the whole playlist.
        Assert.Equal([(YouTubeListId, (int?)1)], reader.Reads);
        Assert.Empty(await db.WishlistItems.IgnoreQueryFilters().ToListAsync());

        // Adding the same playlist again updates that row instead of creating a second one.
        await service.CreateOrUpdateSourceAsync(Owner, WishlistSourceType.YouTubePlaylist, YouTubeListId, autoSync: false, default);
        var only = await db.WishlistSources.IgnoreQueryFilters().SingleAsync();
        Assert.False(only.AutoSync);
    }

    [Fact]
    public async Task CreateOrUpdateSource_YouTubePlaylist_RequiresAListId()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, new FakeSpotifyApi());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateOrUpdateSourceAsync(Owner, WishlistSourceType.YouTubePlaylist, " ", false, default));
    }

    private static async Task<WishlistSource> AddYouTubeSourceAsync(MusicHoarderDbContext db, string name = "Cool music")
    {
        var source = new WishlistSource
        {
            OwnerUserId = Owner,
            SourceType = WishlistSourceType.YouTubePlaylist,
            YouTubePlaylistId = YouTubeListId,
            Name = name,
            AutoSync = true,
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.WishlistSources.Add(source);
        await db.SaveChangesAsync();
        return source;
    }

    private static DeezerCatalogTrack DeezerDetail(string id, string? isrc) =>
        new(id, "Title", "Artist", "Album", 2024, 1, 200_000, isrc);

    private static WishlistService CreateService(
        MusicHoarderDbContext db,
        ISpotifyApiService api,
        IDeezerCatalogService? deezer = null,
        ISpotifyIsrcResolver? resolver = null,
        IYouTubePlaylistReader? youTubePlaylists = null,
        IYouTubeMetadataResolver? youTubeVideos = null) =>
        new(db, api, deezer ?? new FakeDeezerCatalogService(), resolver ?? new FakeSpotifyIsrcResolver(),
            youTubePlaylists ?? new FakeYouTubePlaylistReader(), youTubeVideos ?? new FakeYouTubeMetadataResolver(),
            NullLogger<WishlistService>.Instance);

    private static SpotifyTrackItem Track(string id, string title, string? isrc = null) =>
        new(id, title, "Artist", "Album", null, 200_000, DateTime.UtcNow, isrc);

    private static MusicHoarderDbContext CreateDbContext(string? name = null)
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }

    private sealed class FakeSpotifyApi : ISpotifyApiService
    {
        public List<SpotifyTrackItem> LikedSongs { get; set; } = [];
        public List<SpotifyTrackItem> PlaylistTracks { get; set; } = [];
        public List<SpotifyPlaylistItem> Playlists { get; set; } = [];

        public Task<SpotifyLikedSongsResponse> GetLikedSongsAsync(int offset = 0, int limit = 50, CancellationToken ct = default)
        {
            var page = LikedSongs.Skip(offset).Take(limit).ToList();
            return Task.FromResult(new SpotifyLikedSongsResponse(LikedSongs.Count, offset, limit, page));
        }

        public Task<SpotifyPlaylistsResponse> GetPlaylistsAsync(CancellationToken ct = default) =>
            Task.FromResult(new SpotifyPlaylistsResponse(Playlists));

        public Task<SpotifyPlaylistTracksResponse> GetPlaylistTracksAsync(string playlistId, int offset = 0, int limit = 50, CancellationToken ct = default)
        {
            var page = PlaylistTracks.Skip(offset).Take(limit).ToList();
            return Task.FromResult(new SpotifyPlaylistTracksResponse(PlaylistTracks.Count, offset, limit, page));
        }

        public Task<SpotifyPlaylistLookupResult> GetPlaylistAsync(string playlistId, CancellationToken ct = default)
        {
            var match = Playlists.FirstOrDefault(p => p.SpotifyId == playlistId);
            return Task.FromResult(match is null
                ? new SpotifyPlaylistLookupResult(false, null, true, "not found")
                : new SpotifyPlaylistLookupResult(true, match, false, null));
        }
    }
}
