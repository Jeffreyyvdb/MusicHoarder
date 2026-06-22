using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

public class LrcLibServiceTests
{
    private const string LyricsJson = """
        {"id":36178369,"trackName":"RUBBERZ","artistName":"Fenix Flexin","instrumental":false,
         "plainLyrics":"plain words","syncedLyrics":"[00:01.00] synced words"}
        """;

    [Fact]
    public async Task FetchLyricsAsync_CombinedArtistCredit_FallsBackToPrimaryArtistSearch()
    {
        // The real "RUBBERZ" failure: the stored credit is the combined "Fenix Flexin, Purps On The Beat",
        // which LRCLIB 404s on /get and returns nothing on /search; the track's exact /get also 404s on
        // duration mismatch. Only a /search by the primary artist ("Fenix Flexin") resolves it.
        var handler = new RoutingHandler(req =>
        {
            var query = req.RequestUri!.Query;
            var isCombinedCredit = query.Contains("%2C"); // encoded comma in "A, B"
            var isSearch = req.RequestUri!.AbsolutePath.EndsWith("/search");

            if (isCombinedCredit)
                return isSearch ? Json("[]") : NotFound();   // combined credit matches nothing

            // Primary artist: exact /get still 404s (duration mismatch), but /search resolves.
            return isSearch ? Json($"[{LyricsJson}]") : NotFound();
        });

        var song = Song("RUBBERZ", "Fenix Flexin, Purps On The Beat", 127);

        var result = await CreateService(handler).FetchLyricsAsync(song);

        Assert.NotNull(result);
        Assert.Equal(36178369, result!.LrclibId);
        Assert.Equal("[00:01.00] synced words", result.SyncedLyrics);
        Assert.Equal("plain words", result.PlainLyrics);
    }

    [Fact]
    public async Task FetchLyricsAsync_SoloArtist_ResolvesViaExactGetWithoutFallback()
    {
        var getCalls = 0;
        var handler = new RoutingHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/get"))
            {
                getCalls++;
                return Json(LyricsJson);
            }
            return Json("[]");
        });

        var song = Song("RUBBERZ", "Fenix Flexin", 174);

        var result = await CreateService(handler).FetchLyricsAsync(song);

        Assert.NotNull(result);
        // A single artist credit yields one candidate, so the exact /get is hit exactly once and no
        // search fallback is needed.
        Assert.Equal(1, getCalls);
    }

    [Fact]
    public async Task FetchLyricsAsync_NoMatchAnywhere_ReturnsNull()
    {
        var handler = new RoutingHandler(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/search") ? Json("[]") : NotFound());

        var song = Song("Unknown", "Nobody, Someone Else", 100);

        Assert.Null(await CreateService(handler).FetchLyricsAsync(song));
    }

    private static SongMetadata Song(string title, string artist, int durationSeconds) => new()
    {
        SourcePath = $"/s/{title}.flac",
        FileName = $"{title}.flac",
        Extension = ".flac",
        FileSizeBytes = 1_000,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Title = title,
        Artist = artist,
        DurationSeconds = durationSeconds,
    };

    private static LrcLibService CreateService(RoutingHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://lrclib.net/") };
        return new LrcLibService(httpClient, NullLogger<LrcLibService>.Instance);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(route(request));
    }
}
