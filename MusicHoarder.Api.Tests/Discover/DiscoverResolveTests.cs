using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Tests.Discover;

public class DiscoverResolveTests
{
    private const string Id = "37i9dQZF1DXcBWIGoYBM5M";

    private sealed class Spotify(SpotifyPlaylistLookupResult? result, Exception? throws = null) : ISpotifyApiService
    {
        public Task<SpotifyPlaylistLookupResult> GetPlaylistAsync(string playlistId, CancellationToken ct = default) =>
            throws is null ? Task.FromResult(result!) : Task.FromException<SpotifyPlaylistLookupResult>(throws);

        public Task<SpotifyLikedSongsResponse> GetLikedSongsAsync(int offset = 0, int limit = 50, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SpotifyPlaylistsResponse> GetPlaylistsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SpotifyPlaylistTracksResponse> GetPlaylistTracksAsync(string playlistId, int offset = 0, int limit = 50, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static MusicHoarderDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static T Get<T>(object obj, string name) => (T)obj.GetType().GetProperty(name)!.GetValue(obj)!;

    private static object Value(IResult result) => result.GetType().GetProperty("Value")!.GetValue(result)!;

    [Fact]
    public async Task AnEditorialLinkTheSpotifyApiRefuses_ResolvesThroughTheArchive()
    {
        await using var db = NewContext();
        var archive = new FakeSpotifyPlaylistArchive()
            .With(FakeSpotifyPlaylistArchive.Playlist(Id), FakeSpotifyPlaylistArchive.Track("0aaaaaaaaaaaaaaaaaaaa1", "One"), FakeSpotifyPlaylistArchive.Track("0aaaaaaaaaaaaaaaaaaaa2", "Two"));
        archive.Covers[Id] = "https://cdn/cover";
        var refused = new Spotify(new SpotifyPlaylistLookupResult(false, null, true, "blocked"));

        var result = await DiscoverEndpoints.ResolveSpotifyAsync(Id, refused, archive, db, NullLogger.Instance, default);

        var body = Value(result);
        Assert.Equal(("spotifyArchive", Id, "Today's Top Hits", "https://cdn/cover", 2, false),
            (Get<string>(body, "Provider"), Get<string>(body, "PlaylistId"), Get<string>(body, "Title"), Get<string>(body, "CoverUrl"), Get<int>(body, "TrackCount"), Get<bool>(body, "Subscribed")));
    }

    [Fact]
    public async Task WhenSpotifyIsNotConnected_TheArchiveStillAnswers()
    {
        await using var db = NewContext();
        db.WishlistSources.Add(new WishlistSource
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            SourceType = WishlistSourceType.SpotifyArchivePlaylist,
            SpotifyPlaylistId = Id,
            Name = "x",
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var archive = new FakeSpotifyPlaylistArchive().With(FakeSpotifyPlaylistArchive.Playlist(Id));

        var result = await DiscoverEndpoints.ResolveSpotifyAsync(Id, new Spotify(null, new SpotifyNotConnectedException()), archive, db, NullLogger.Instance, default);

        var body = Value(result);
        Assert.Equal("spotifyArchive", Get<string>(body, "Provider"));
        Assert.True(Get<bool>(body, "Subscribed"));
    }

    [Fact]
    public async Task APlaylistTheApiCanRead_StaysAPlainSpotifyPlaylist()
    {
        await using var db = NewContext();
        var live = new Spotify(new SpotifyPlaylistLookupResult(true, new SpotifyPlaylistItem(Id, "Mine", null, null, 12, null), false, null));

        var result = await DiscoverEndpoints.ResolveSpotifyAsync(Id, live, new FakeSpotifyPlaylistArchive(), db, NullLogger.Instance, default);

        Assert.Equal("spotify", Get<string>(Value(result), "Provider"));
    }

    [Fact]
    public async Task NeitherSource_KeepsTheEditorialBlockedError()
    {
        await using var db = NewContext();
        var refused = new Spotify(new SpotifyPlaylistLookupResult(false, null, true, "blocked"));

        var result = await DiscoverEndpoints.ResolveSpotifyAsync(Id, refused, new FakeSpotifyPlaylistArchive(), db, NullLogger.Instance, default);

        Assert.Equal(422, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Equal("spotify_editorial_blocked", Get<string>(Value(result), "error"));
    }
}
