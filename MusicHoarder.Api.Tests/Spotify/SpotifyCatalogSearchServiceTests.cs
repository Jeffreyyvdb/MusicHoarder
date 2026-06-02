using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Tests.Spotify;

public class SpotifyCatalogSearchServiceTests
{
    [Fact]
    public async Task SearchTracksAsync_ParsesTrack_IncludingIsrcAlbumYear()
    {
        var handler = new FakeHttpHandler();
        handler.EnqueueJsonResponse("""{"access_token":"tok","token_type":"Bearer","expires_in":3600}""");
        handler.EnqueueJsonResponse(
            """
            {
              "tracks": {
                "items": [
                  {
                    "id": "track1",
                    "name": "Song Name",
                    "duration_ms": 200000,
                    "track_number": 4,
                    "artists": [{"name": "Artist One"}, {"name": "Artist Two"}],
                    "album": {
                      "name": "Album A",
                      "release_date": "2019-03-15"
                    },
                    "external_ids": { "isrc": "USRC17607839" }
                  }
                ]
              }
            }
            """);

        var service = CreateService(handler);
        var tracks = await service.SearchTracksAsync("cid", "sec", "artist song", CancellationToken.None);

        Assert.Single(tracks);
        var t = tracks[0];
        Assert.Equal("track1", t.Id);
        Assert.Equal("Song Name", t.Title);
        Assert.Equal("Artist One, Artist Two", t.Artist);
        Assert.Equal("Album A", t.AlbumName);
        Assert.Equal(2019, t.ReleaseYear);
        Assert.Equal(4, t.TrackNumber);
        Assert.Equal(200000, t.DurationMs);
        Assert.Equal("USRC17607839", t.Isrc);
        Assert.Equal(2, handler.SendCount);
    }

    [Fact]
    public async Task SearchTracksAsync_SecondCallSameQuery_UsesSearchCache_SkipsHttp()
    {
        var handler = new FakeHttpHandler();
        handler.EnqueueJsonResponse("""{"access_token":"tok","token_type":"Bearer","expires_in":3600}""");
        handler.EnqueueJsonResponse("""{"tracks":{"items":[{"id":"a","name":"N","duration_ms":1,"artists":[{"name":"X"}],"album":{"name":"Al","release_date":"2020"}}]}}""");

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(handler, cache);

        _ = await service.SearchTracksAsync("cid", "sec", "same query", CancellationToken.None);
        var firstCount = handler.SendCount;

        _ = await service.SearchTracksAsync("cid", "sec", "same query", CancellationToken.None);

        Assert.Equal(firstCount, handler.SendCount);
    }

    [Fact]
    public async Task SearchTracksAsync_DifferentQuery_AfterCache_StillUsesTokenCache_OneHttpCall()
    {
        var handler = new FakeHttpHandler();
        handler.EnqueueJsonResponse("""{"access_token":"tok","token_type":"Bearer","expires_in":3600}""");
        handler.EnqueueJsonResponse("""{"tracks":{"items":[{"id":"a","name":"N","duration_ms":1,"artists":[{"name":"X"}],"album":{"name":"Al","release_date":"2020"}}]}}""");
        handler.EnqueueJsonResponse("""{"tracks":{"items":[{"id":"b","name":"M","duration_ms":2,"artists":[{"name":"Y"}],"album":{"name":"Bl","release_date":"2021"}}]}}""");

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(handler, cache);

        _ = await service.SearchTracksAsync("cid", "sec", "query one", CancellationToken.None);
        Assert.Equal(2, handler.SendCount);

        _ = await service.SearchTracksAsync("cid", "sec", "query two", CancellationToken.None);
        Assert.Equal(3, handler.SendCount);
    }

    [Fact]
    public async Task SearchTracksAsync_EmptyCredentials_ReturnsEmpty()
    {
        var handler = new FakeHttpHandler();
        var service = CreateService(handler);
        var tracks = await service.SearchTracksAsync("", "", "q", CancellationToken.None);
        Assert.Empty(tracks);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task GetAlbumAsync_ParsesTracklistWithDiscAndTrackNumbers()
    {
        var handler = new FakeHttpHandler();
        handler.EnqueueJsonResponse("""{"access_token":"tok","token_type":"Bearer","expires_in":3600}""");
        handler.EnqueueJsonResponse(
            """
            {
              "id": "alb-1",
              "name": "Discovery",
              "release_date": "2001-03-12",
              "artists": [{"name": "Daft Punk"}],
              "images": [{"url": "https://img/cover.jpg"}],
              "tracks": {
                "items": [
                  {"id": "t1", "name": "One More Time", "disc_number": 1, "track_number": 1, "duration_ms": 320000},
                  {"id": "t2", "name": "Aerodynamic", "disc_number": 1, "track_number": 2, "duration_ms": 210000}
                ]
              }
            }
            """);

        var album = await CreateService(handler).GetAlbumAsync("cid", "sec", "alb-1", CancellationToken.None);

        Assert.NotNull(album);
        Assert.Equal("alb-1", album!.Id);
        Assert.Equal("Discovery", album.Name);
        Assert.Equal("Daft Punk", album.Artist);
        Assert.Equal(2001, album.Year);
        Assert.Equal("https://img/cover.jpg", album.ImageUrl);
        Assert.Equal(2, album.Tracks.Count);
        Assert.Equal((1, 1, "One More Time", 320000, "t1"),
            (album.Tracks[0].DiscNumber, album.Tracks[0].TrackNumber, album.Tracks[0].Title, album.Tracks[0].DurationMs, album.Tracks[0].Id));
    }

    private static SpotifyCatalogSearchService CreateService(FakeHttpHandler handler, IMemoryCache? cache = null)
    {
        cache ??= new MemoryCache(new MemoryCacheOptions());
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var opts = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/s",
            DestinationDirectory = "/d",
            SpotifyApiRequestsPerSecond = 20,
            SpotifyApiSearchLimit = 10,
            SpotifyApiSearchCacheMinutes = 60
        });
        return new SpotifyCatalogSearchService(httpClient, cache, opts, NullLogger<SpotifyCatalogSearchService>.Instance);
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public int SendCount { get; private set; }

        public void EnqueueJsonResponse(string json)
        {
            _responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
