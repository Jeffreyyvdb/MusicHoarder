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

    // --- Duration gating on the /search fallback ---
    //
    // /api/get is handed a &duration= and LRCLIB enforces it. /api/search is keyed on track name alone, so it
    // returns live cuts, sped-up edits and extended mixes of the same song — the right words on a clock that
    // has nothing to do with our audio. That is the single biggest source of lyrics whose timestamps are
    // wildly off, and it is free to prevent.

    private static string Entry(int id, double duration, string synced) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $$"""
          {"id":{{id}},"trackName":"RUBBERZ","artistName":"Fenix Flexin","instrumental":false,
           "duration":{{duration}},"plainLyrics":"plain words","syncedLyrics":"{{synced}}"}
          """);

    [Fact]
    public async Task FetchLyricsAsync_SearchHitForADifferentLengthRecording_IsRejected()
    {
        // The only hit is a 5:20 live version of our 2:54 track. Its lyrics are right and its timing is not.
        var handler = new RoutingHandler(req => req.RequestUri!.AbsolutePath.EndsWith("/search")
            ? Json($"[{Entry(1, 320, "[00:01.00] live version")}]")
            : NotFound());

        Assert.Null(await CreateService(handler).FetchLyricsAsync(Song("RUBBERZ", "Fenix Flexin", 174)));
    }

    [Fact]
    public async Task FetchLyricsAsync_SearchPrefersTheEntryClosestToOurLength()
    {
        // A remix, our recording, and a radio edit all come back. Only one was timed against our audio.
        var handler = new RoutingHandler(req => req.RequestUri!.AbsolutePath.EndsWith("/search")
            ? Json($"[{Entry(1, 320, "[00:01.00] remix")},{Entry(2, 175, "[00:01.00] ours")},{Entry(3, 140, "[00:01.00] radio edit")}]")
            : NotFound());

        var result = await CreateService(handler).FetchLyricsAsync(Song("RUBBERZ", "Fenix Flexin", 174));

        Assert.Equal(2, result!.LrclibId);
        Assert.Equal("[00:01.00] ours", result.SyncedLyrics);
        Assert.Equal(175, result.DurationSeconds);
    }

    [Fact]
    public async Task FetchLyricsAsync_SearchHitsWithNoDurationAtAll_AreStillAccepted()
    {
        // Absence of evidence is not evidence: an entry that simply omits the field must not be discarded
        // over it. The stored-lyrics timing check downstream still gets its say.
        var handler = new RoutingHandler(req => req.RequestUri!.AbsolutePath.EndsWith("/search")
            ? Json($"[{LyricsJson}]")
            : NotFound());

        var result = await CreateService(handler).FetchLyricsAsync(Song("RUBBERZ", "Fenix Flexin", 174));

        Assert.NotNull(result);
        Assert.Null(result!.DurationSeconds);
    }

    [Fact]
    public async Task FetchLyricsAsync_CarriesTheMatchedEntrysDurationForTheTimingCheck()
    {
        var handler = new RoutingHandler(req => req.RequestUri!.AbsolutePath.EndsWith("/get")
            ? Json(Entry(9, 174.5, "[00:01.00] ours"))
            : Json("[]"));

        var result = await CreateService(handler).FetchLyricsAsync(Song("RUBBERZ", "Fenix Flexin", 174));

        Assert.Equal(174.5, result!.DurationSeconds);
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
