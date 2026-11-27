using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.CleanEdits;

namespace MusicHoarder.Api.Tests.CleanEdits;

public class EditionReferenceClientTests
{
    private const string ITunesSearch = """
        {"results":[
          {"trackId":1,"trackName":"Stronger","trackExplicitness":"explicit","trackTimeMillis":311500,"previewUrl":"https://a/1.m4a"},
          {"trackId":2,"trackName":"Stronger","trackExplicitness":"cleaned","trackTimeMillis":311500,"previewUrl":"https://a/2.m4a"},
          {"trackId":3,"trackName":"Stronger","trackExplicitness":"notExplicit","trackTimeMillis":311500,"previewUrl":"https://a/3.m4a"},
          {"trackId":4,"trackName":"Stronger (Live)","trackExplicitness":"explicit","trackTimeMillis":311500,"previewUrl":"https://a/4.m4a"},
          {"trackId":5,"trackName":"Stronger","trackExplicitness":"explicit","trackTimeMillis":400000,"previewUrl":"https://a/5.m4a"},
          {"trackId":6,"trackName":"Stronger","trackExplicitness":"cleaned","trackTimeMillis":311500}
        ]}
        """;

    private const string DeezerByIsrc = """{"id":10,"title":"Stronger","duration":313,"preview":"https://d/10.mp3","explicit_content_lyrics":1}""";

    private const string DeezerSearch = """
        {"data":[
          {"id":10,"title":"Stronger","duration":313,"preview":"https://d/10.mp3","explicit_content_lyrics":1},
          {"id":11,"title":"Stronger","duration":313,"preview":"https://d/11.mp3","explicit_content_lyrics":3},
          {"id":12,"title":"Stronger","duration":313,"preview":"https://d/12.mp3","explicit_content_lyrics":0},
          {"id":13,"title":"Stronger","duration":200,"preview":"https://d/13.mp3","explicit_content_lyrics":1}
        ]}
        """;

    private static readonly ReferenceQuery Query = new("Kanye West", "Stronger", "USUM70741299", [313]);

    private static EditionReferenceClient Client(Func<string, (HttpStatusCode, string)> respond) =>
        new(new HttpClient(new StubHandler(respond)), NullLogger<EditionReferenceClient>.Instance);

    private static (HttpStatusCode, string) Healthy(string url) => url switch
    {
        _ when url.StartsWith("https://itunes.apple.com/search") => (HttpStatusCode.OK, ITunesSearch),
        _ when url.Contains("/track/isrc:") => (HttpStatusCode.OK, DeezerByIsrc),
        _ when url.StartsWith("https://api.deezer.com/search") => (HttpStatusCode.OK, DeezerSearch),
        _ => (HttpStatusCode.NotFound, ""),
    };

    [Fact]
    public async Task Only_previews_of_this_recording_in_a_known_edition_count()
    {
        var lookup = await Client(Healthy).FindAsync(Query, CancellationToken.None);

        Assert.False(lookup.Incomplete);
        Assert.Equal(
            [
                (Edition.Explicit, "itunes:1"),
                (Edition.Clean, "itunes:2"),
                (Edition.Explicit, "deezer:10"),
                (Edition.Clean, "deezer:11"),
            ],
            lookup.References.Select(r => (r.Edition, r.Id)).ToArray());
    }

    [Fact]
    public async Task A_preview_may_match_the_length_asked_for_or_the_files()
    {
        var lookup = await Client(Healthy).FindAsync(Query with { DurationsSeconds = [200, 313] }, CancellationToken.None);

        Assert.Equal(
            ["itunes:1", "itunes:2", "deezer:10", "deezer:11", "deezer:13"],
            lookup.References.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task A_catalog_that_fails_makes_the_lookup_incomplete_and_the_other_still_answers()
    {
        var lookup = await Client(url => url.StartsWith("https://itunes.apple.com")
                ? (HttpStatusCode.TooManyRequests, "")
                : Healthy(url))
            .FindAsync(Query, CancellationToken.None);

        Assert.True(lookup.Incomplete);
        Assert.Equal(["deezer:10", "deezer:11"], lookup.References.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task A_deezer_quota_answer_makes_the_lookup_incomplete()
    {
        var lookup = await Client(url => url.Contains("deezer.com")
                ? (HttpStatusCode.OK, """{"error":{"type":"Exception","message":"Quota limit exceeded","code":4}}""")
                : Healthy(url))
            .FindAsync(Query, CancellationToken.None);

        Assert.True(lookup.Incomplete);
        Assert.Equal(["itunes:1", "itunes:2"], lookup.References.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task A_song_the_catalogs_do_not_know_is_a_complete_empty_answer()
    {
        var lookup = await Client(url => url.Contains("/track/isrc:")
                ? (HttpStatusCode.OK, """{"error":{"type":"DataException","message":"no data","code":800}}""")
                : url.StartsWith("https://api.deezer.com/search") ? (HttpStatusCode.OK, """{"data":[],"total":0}""")
                : (HttpStatusCode.OK, """{"resultCount":0,"results":[]}"""))
            .FindAsync(Query, CancellationToken.None);

        Assert.False(lookup.Incomplete);
        Assert.Empty(lookup.References);
    }

    [Fact]
    public async Task An_unreachable_catalog_never_throws()
    {
        var client = new EditionReferenceClient(
            new HttpClient(new StubHandler(_ => throw new HttpRequestException("no route"))),
            NullLogger<EditionReferenceClient>.Instance);

        var lookup = await client.FindAsync(Query, CancellationToken.None);

        Assert.True(lookup.Incomplete);
        Assert.Empty(lookup.References);
    }

    [Fact]
    public async Task A_preview_that_cannot_be_fetched_is_null_rather_than_an_error()
    {
        var client = Client(_ => (HttpStatusCode.NotFound, ""));

        Assert.Null(await client.DownloadPreviewAsync("https://a/1.m4a", CancellationToken.None));
        Assert.Null(await client.DownloadPreviewAsync("file:///etc/passwd", CancellationToken.None));
    }

    [Fact]
    public async Task A_preview_is_returned_as_bytes()
    {
        var client = Client(_ => (HttpStatusCode.OK, "audio"));

        Assert.Equal("audio"u8.ToArray(), await client.DownloadPreviewAsync("https://a/1.m4a", CancellationToken.None));
    }

    private sealed class StubHandler(Func<string, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var (status, body) = respond(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
