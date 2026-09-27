using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Tests.Endpoints;

public class PlaylistCollectionsEndpointTests
{
    private static MusicHoarderDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static async Task<PlaylistCollectionsResponse> Get(MusicHoarderDbContext db, ISpotifyApiService api)
    {
        var result = await PlaylistSyncEndpoints.GetCollections(api, db, NullLoggerFactory.Instance, default);
        return (PlaylistCollectionsResponse)((IValueHttpResult)result).Value!;
    }

    private static void Subscribe(MusicHoarderDbContext db, string playlistId, string name, int total)
    {
        db.ExportedPlaylists.Add(new ExportedPlaylist
        {
            OwnerUserId = WellKnownUsers.OwnerId,
            Kind = ExportedPlaylistKind.Playlist,
            SpotifyPlaylistId = playlistId,
            Name = name,
            FilePath = $"/dest/Playlists/{name}.m3u8",
            SpotifyTrackTotal = total,
            MatchedTrackCount = 1,
            LastGeneratedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task Lists_a_synced_playlist_that_spotify_no_longer_lists_so_it_can_be_switched_off()
    {
        await using var db = NewContext();
        Subscribe(db, "listed", "Beta", 3);
        Subscribe(db, "unlisted", "Alpha", 7);

        var api = new StubApi(new SpotifyPlaylistsResponse(new[]
        {
            new SpotifyPlaylistItem("listed", "Beta", null, "https://img/beta", 3, "me"),
            new SpotifyPlaylistItem("other", "Gamma", null, null, 5, "someone"),
        }));

        var response = await Get(db, api);

        Assert.True(response.SpotifyConnected);
        Assert.Null(response.SpotifyError);
        // Liked Songs first, then every playlist by name, the unlisted subscription among them.
        Assert.Equal(["Liked Songs", "Alpha", "Beta", "Gamma"], response.Collections.Select(c => c.Name));

        var unlisted = response.Collections.Single(c => c.SpotifyPlaylistId == "unlisted");
        Assert.True(unlisted.Subscribed);
        Assert.NotNull(unlisted.Id);
        Assert.Equal(7, unlisted.SpotifyTrackTotal);

        Assert.True(response.Collections.Single(c => c.SpotifyPlaylistId == "listed").Subscribed);
        Assert.False(response.Collections.Single(c => c.SpotifyPlaylistId == "other").Subscribed);
    }

    [Fact]
    public async Task Does_not_list_a_synced_playlist_twice()
    {
        await using var db = NewContext();
        Subscribe(db, "listed", "Beta", 3);

        var api = new StubApi(new SpotifyPlaylistsResponse(new[]
        {
            new SpotifyPlaylistItem("listed", "Beta", null, null, 3, "me"),
        }));

        var response = await Get(db, api);

        Assert.Single(response.Collections, c => c.SpotifyPlaylistId == "listed");
    }

    private sealed class StubApi(SpotifyPlaylistsResponse playlists) : ISpotifyApiService
    {
        public Task<SpotifyLikedSongsResponse> GetLikedSongsAsync(int offset = 0, int limit = 50, CancellationToken ct = default) =>
            Task.FromResult(new SpotifyLikedSongsResponse(0, offset, limit, []));

        public Task<SpotifyPlaylistsResponse> GetPlaylistsAsync(CancellationToken ct = default) =>
            Task.FromResult(playlists);

        public Task<SpotifyPlaylistTracksResponse> GetPlaylistTracksAsync(string playlistId, int offset = 0, int limit = 50, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<SpotifyPlaylistLookupResult> GetPlaylistAsync(string playlistId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
