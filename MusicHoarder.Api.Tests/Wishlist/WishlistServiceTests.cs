using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;
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
        var service = new WishlistService(db, api, NullLogger<WishlistService>.Instance);

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
        var service = new WishlistService(db, new FakeSpotifyApi(), NullLogger<WishlistService>.Instance);

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
        var service = new WishlistService(db, api, NullLogger<WishlistService>.Instance);

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
        var service = new WishlistService(db, api, NullLogger<WishlistService>.Instance);

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
        var service = new WishlistService(db, api, NullLogger<WishlistService>.Instance);

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
        var service = new WishlistService(db, api, NullLogger<WishlistService>.Instance);

        var result = await service.SyncSourceAsync(Owner, source, default, maxPages: 2);

        Assert.Equal(100, result.Added);
        Assert.Equal(100, await db.WishlistItems.IgnoreQueryFilters().CountAsync());
    }

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
    }
}
