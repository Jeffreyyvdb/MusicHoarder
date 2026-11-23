using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Playback;

namespace MusicHoarder.Api.Tests.Enrichment;

public class GeniusLyricsServiceTests
{
    private const string SearchJson = """
        {"response":{"sections":[{"type":"song","hits":[
          {"type":"song","result":{"id":11,"title":"Tití Me Preguntó (English Translation)","lyrics_state":"complete","instrumental":false,"primary_artist":{"name":"Genius English Translations"}}},
          {"type":"song","result":{"id":22,"title":"MANNSCHAFT","lyrics_state":"complete","instrumental":false,"primary_artist":{"name":"JoeyAK"}}}
        ]}]}}
        """;

    private static string Page(string body) => $"""<div data-lyrics-container="true">{body}</div>""";

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(respond(request));
        }
    }

    private const string IntroSearchJson = """
        {"response":{"sections":[{"type":"song","hits":[
          {"type":"song","result":{"id":120581,"title":"Intro","lyrics_state":"complete","instrumental":false,"primary_artist":{"name":"Kanye West"},"release_date_components":{"year":2004,"month":2,"day":10}}},
          {"type":"song","result":{"id":7,"title":"Intro","lyrics_state":"complete","instrumental":false,"primary_artist":{"name":"Kanye West"},"release_date_components":{"year":null}}},
          {"type":"song","result":{"id":8,"title":"Intro","lyrics_state":"complete","instrumental":false,"primary_artist":{"name":"Kanye West"},"release_date_components":null}}
        ]}]}}
        """;

    private static readonly DateTimeOffset Start = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    // Throttling is off in most tests: at the default 1 request a second every refusal test takes seconds.
    private static GeniusLyricsService Create(Handler handler, TimeProvider? clock = null, int requestsPerSecond = 1000) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://genius.com/") },
        Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions { GeniusRequestsPerSecond = requestsPerSecond }),
        NullLogger<GeniusLyricsService>.Instance,
        clock ?? new ManualTimeProvider(Start));

    private static HttpResponseMessage Ok(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };

    private static HttpResponseMessage Status(HttpStatusCode status, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status);
        if (retryAfter is { } delta)
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delta);
        return response;
    }

    /// <summary>Answers each search with the next response in line, and every page with lyrics.</summary>
    private static Handler Answering(params Func<HttpResponseMessage>[] searches)
    {
        var next = new Queue<Func<HttpResponseMessage>>(searches);
        return new Handler(r => r.RequestUri!.AbsolutePath.StartsWith("/api/search")
            ? next.Dequeue()()
            : Ok(Page("words")));
    }

    private static Func<HttpResponseMessage>[] Refusals(int count, HttpStatusCode status = HttpStatusCode.Forbidden) =>
        Enumerable.Repeat<Func<HttpResponseMessage>>(() => Status(status), count).ToArray();

    private static async Task FetchTimes(GeniusLyricsService service, int count)
    {
        for (var i = 0; i < count; i++)
            await service.FetchLyricsAsync(Song());
    }

    private static SongMetadata Song(
        string artist = "JoeyAK, THIZZY52", string title = "Mannschaft (feat. THIZZY52)", int? year = null) =>
        new()
        {
            SourcePath = "/src/a.flac",
            FileName = "a.flac",
            Extension = ".flac",
            FileSizeBytes = 1L,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = artist,
            Title = title,
            Year = year,
        };

    [Fact]
    public async Task ConfidentHit_ReturnsItsPlainLyricsLabelledGenius()
    {
        var handler = new Handler(r => r.RequestUri!.AbsolutePath.StartsWith("/api/search")
            ? Ok(SearchJson)
            : Ok(Page("[Intro]<br/>Scheiße<br/>Eyo")));

        var result = await Create(handler).FetchLyricsAsync(Song());

        Assert.NotNull(result);
        Assert.Equal("Scheiße\nEyo", result.PlainLyrics);
        Assert.Null(result.SyncedLyrics);
        Assert.False(result.IsInstrumental);
        Assert.Equal(LyricsOrigin.Genius, result.Origin);
        Assert.Equal(22, result.GeniusSongId);
        Assert.Contains("q=JoeyAK%20Mannschaft", handler.Requests[0]);
        Assert.Equal("/songs/22", handler.Requests[1]);
    }

    [Fact]
    public async Task NoConfidentHit_IsNullWithoutFetchingAPage()
    {
        var handler = new Handler(_ => Ok(SearchJson));

        Assert.Null(await Create(handler).FetchLyricsAsync(Song("Someone Else", "Other Song")));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task SearchRefused_IsNull(HttpStatusCode status)
    {
        var handler = new Handler(_ => new HttpResponseMessage(status));

        Assert.Null(await Create(handler).FetchLyricsAsync(Song()));
    }

    [Fact]
    public async Task FiveRefusalsInARow_StopTheNextFetchFromAskingGenius()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var service = Create(handler);

        for (var i = 0; i < 6; i++)
            Assert.Null(await service.FetchLyricsAsync(Song()));

        Assert.Equal(5, handler.Requests.Count);
    }

    [Fact]
    public async Task CoolDown_EndsAfterAnHour_AndARefusedProbeDoublesIt()
    {
        var clock = new ManualTimeProvider(Start);
        var handler = Answering(Refusals(7));
        var service = Create(handler, clock);
        await FetchTimes(service, 5);

        clock.Advance(TimeSpan.FromMinutes(59));
        await FetchTimes(service, 1);
        Assert.Equal(5, handler.Requests.Count);

        clock.Advance(TimeSpan.FromMinutes(1));
        await FetchTimes(service, 2);
        Assert.Equal(6, handler.Requests.Count);

        clock.Advance(TimeSpan.FromMinutes(119));
        await FetchTimes(service, 1);
        Assert.Equal(6, handler.Requests.Count);

        clock.Advance(TimeSpan.FromMinutes(1));
        await FetchTimes(service, 1);
        Assert.Equal(7, handler.Requests.Count);
    }

    [Fact]
    public async Task CoolDown_LastsAtLeastAsLongAsRetryAfterAsks()
    {
        var clock = new ManualTimeProvider(Start);
        var handler = Answering([.. Refusals(4, HttpStatusCode.TooManyRequests),
            () => Status(HttpStatusCode.TooManyRequests, TimeSpan.FromHours(3)), () => Ok(SearchJson)]);
        var service = Create(handler, clock);
        await FetchTimes(service, 5);

        clock.Advance(TimeSpan.FromHours(3) - TimeSpan.FromSeconds(1));
        await FetchTimes(service, 1);
        Assert.Equal(5, handler.Requests.Count);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.NotNull(await service.FetchLyricsAsync(Song()));
    }

    [Fact]
    public async Task AnAnswer_StartsTheRefusalCountOver()
    {
        var handler = Answering([.. Refusals(4), () => Ok(SearchJson), .. Refusals(5)]);
        var service = Create(handler);

        await FetchTimes(service, 9);
        Assert.Equal(10, handler.Requests.Count);

        await FetchTimes(service, 2);
        Assert.Equal(11, handler.Requests.Count);
    }

    [Fact]
    public async Task AnAnswer_AfterACoolDown_StartsItsLengthOverAtAnHour()
    {
        var clock = new ManualTimeProvider(Start);
        var handler = Answering([.. Refusals(5), () => Ok(SearchJson), .. Refusals(6)]);
        var service = Create(handler, clock);
        await FetchTimes(service, 5);
        clock.Advance(TimeSpan.FromHours(1));

        await FetchTimes(service, 6);
        clock.Advance(TimeSpan.FromHours(1));
        await FetchTimes(service, 1);

        Assert.Equal(13, handler.Requests.Count);
    }

    [Fact]
    public async Task ServerErrors_NeitherCountNorStartTheCountOver()
    {
        var handler = Answering([.. Refusals(4), .. Refusals(10, HttpStatusCode.BadGateway), .. Refusals(1)]);
        var service = Create(handler);

        await FetchTimes(service, 16);

        Assert.Equal(15, handler.Requests.Count);
    }

    [Fact]
    public async Task Requests_AreThrottledToTheConfiguredRate()
    {
        var handler = new Handler(r => r.RequestUri!.AbsolutePath.StartsWith("/api/search")
            ? Ok(SearchJson)
            : Ok(Page("words")));
        var service = Create(handler, requestsPerSecond: 1);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        Assert.NotNull(await service.FetchLyricsAsync(Song()));

        Assert.Equal(2, handler.Requests.Count);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(500), $"two requests took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task GenericTitle_TakesOnlyTheHitFromTheSongsYear()
    {
        var handler = new Handler(r => r.RequestUri!.AbsolutePath.StartsWith("/api/search")
            ? Ok(IntroSearchJson)
            : Ok(Page("words")));

        var result = await Create(handler).FetchLyricsAsync(Song("Kanye West", "Intro", 2004));

        Assert.Equal(120581, result?.GeniusSongId);
    }

    [Theory]
    [InlineData(2007)]
    [InlineData(null)]
    public async Task GenericTitle_FromAnotherOrAnUnknownYear_MatchesNothing(int? year)
    {
        var handler = new Handler(_ => Ok(IntroSearchJson));

        Assert.Null(await Create(handler).FetchLyricsAsync(Song("Kanye West", "Intro", year)));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task PageRefusedOrUnreadable_IsNull()
    {
        var refused = new Handler(r => r.RequestUri!.AbsolutePath.StartsWith("/api/search")
            ? Ok(SearchJson)
            : new HttpResponseMessage(HttpStatusCode.Forbidden));
        var challenge = new Handler(r => r.RequestUri!.AbsolutePath.StartsWith("/api/search")
            ? Ok(SearchJson)
            : Ok("<html>Just a moment...</html>"));

        Assert.Null(await Create(refused).FetchLyricsAsync(Song()));
        Assert.Null(await Create(challenge).FetchLyricsAsync(Song()));
    }

    [Fact]
    public async Task MalformedSearchBody_IsNull()
    {
        Assert.Null(await Create(new Handler(_ => Ok("<html>not json</html>"))).FetchLyricsAsync(Song()));
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Create(new Handler(_ => Ok(SearchJson))).FetchLyricsAsync(Song(), cts.Token));
    }
}
