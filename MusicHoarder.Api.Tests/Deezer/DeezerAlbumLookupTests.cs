using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.Deezer;

public class DeezerAlbumLookupTests
{
    [Fact]
    public async Task GetAlbumAsync_ParsesTracklistAndConvertsSecondsToMs()
    {
        var handler = new StubHandler("""
            {
              "id": 302127,
              "title": "Discovery",
              "release_date": "2001-03-07",
              "cover_xl": "https://cover/xl.jpg",
              "artist": { "name": "Daft Punk" },
              "tracks": {
                "data": [
                  { "id": 1, "title": "One More Time", "duration": 320, "track_position": 1, "disk_number": 1 },
                  { "id": 2, "title": "Aerodynamic", "duration": 210, "track_position": 2, "disk_number": 1 }
                ]
              }
            }
            """);

        var album = await CreateService(handler).GetAlbumAsync("302127");

        Assert.NotNull(album);
        Assert.Equal("302127", album!.Id);
        Assert.Equal("Discovery", album.Title);
        Assert.Equal("Daft Punk", album.Artist);
        Assert.Equal(2001, album.Year);
        Assert.Equal("https://cover/xl.jpg", album.CoverUrl);
        Assert.Equal(2, album.Tracks.Count);
        // Deezer reports seconds; the service converts to milliseconds.
        Assert.Equal(320000, album.Tracks[0].DurationMs);
        Assert.Equal((1, 1, "One More Time"), (album.Tracks[0].DiscNumber, album.Tracks[0].TrackNumber, album.Tracks[0].Title));
    }

    [Fact]
    public async Task SearchAlbumIdAsync_ReturnsFirstHit()
    {
        var handler = new StubHandler("""{"data":[{"id":302127,"title":"Discovery"},{"id":999,"title":"Other"}]}""");
        var id = await CreateService(handler).SearchAlbumIdAsync("Daft Punk", "Discovery");
        Assert.Equal("302127", id);
    }

    private static DeezerCatalogService CreateService(StubHandler handler)
    {
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var opts = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/s",
            DestinationDirectory = "/d",
            DeezerApiRequestsPerSecond = 20,
        });
        return new DeezerCatalogService(httpClient, new MemoryCache(new MemoryCacheOptions()), opts, NullLogger<DeezerCatalogService>.Instance);
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }
}
