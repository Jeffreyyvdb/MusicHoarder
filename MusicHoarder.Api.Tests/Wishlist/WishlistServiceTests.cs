using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;
using MusicHoarder.Api.Tests.Deezer;
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

    private static DeezerCatalogTrack DeezerDetail(string id, string? isrc) =>
        new(id, "Title", "Artist", "Album", 2024, 1, 200_000, isrc);

    private static WishlistService CreateService(
        MusicHoarderDbContext db,
        ISpotifyApiService api,
        IDeezerCatalogService? deezer = null,
        ISpotifyIsrcResolver? resolver = null) =>
        new(db, api, deezer ?? new FakeDeezerCatalogService(), resolver ?? new FakeSpotifyIsrcResolver(),
            NullLogger<WishlistService>.Instance);

    private static SpotifyTrackItem Track(string id, string title, string? isrc = null) =>
        new(id, title, "Artist", "Album", null, 200_000, DateTime.UtcNow, isrc);

    private static MusicHoarderDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
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
