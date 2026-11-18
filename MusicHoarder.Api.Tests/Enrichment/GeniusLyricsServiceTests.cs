using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

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

    private static GeniusLyricsService Create(Handler handler) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://genius.com/") },
        Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions()),
        NullLogger<GeniusLyricsService>.Instance);

    private static HttpResponseMessage Ok(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };

    private static SongMetadata Song(string artist = "JoeyAK, THIZZY52", string title = "Mannschaft (feat. THIZZY52)") =>
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
