using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.Deezer;

public class DeezerPlaylistTracksTests
{
    [Fact]
    public async Task GetPlaylistTracksAsync_ParsesTracksAndConvertsSecondsToMs()
    {
        // Single page: no `next`, so paging stops after the first fetch.
        var handler = new StubHandler("""
            { "data": [
                { "id": 1, "title": "Song One", "duration": 200,
                  "artist": { "name": "Artist A" },
                  "album": { "title": "Album A", "cover_medium": "https://a/1.jpg" } },
                { "id": 2, "title": "Song Two", "duration": 180,
                  "artist": { "name": "Artist B" }, "album": { "title": "Album B" } }
            ] }
            """);

        var result = await CreateService(handler).GetPlaylistTracksAsync("pl1");
        var tracks = result.Tracks;

        Assert.True(result.IsComplete);
        Assert.Equal(2, tracks.Count);
        Assert.Equal(("1", "Song One", "Artist A", "Album A"), (tracks[0].Id, tracks[0].Title, tracks[0].Artist, tracks[0].Album));
        Assert.Equal(200_000, tracks[0].DurationMs);
        Assert.Equal("https://a/1.jpg", tracks[0].CoverUrl);
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
