using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Catalog;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Tests.Deezer;

namespace MusicHoarder.Api.Tests.Catalog;

public class CatalogAudioResolverTests
{
    private static (CatalogAudioResolver Resolver, FakeLocator Locator) Build(
        FakeLocator? locator = null, bool knownTrack = true, Exception? lookupFailure = null)
    {
        var deezer = new FakeDeezerCatalogService { LookupFailure = lookupFailure };
        if (knownTrack)
            deezer.TracksById["42"] = new DeezerCatalogTrack("42", "Harder", "Daft Punk", "Discovery", 2001, 4, 224_000, "GBDUW0000059");
        locator ??= new FakeLocator(_ => CatalogTestData.Audio());
        var resolver = new CatalogAudioResolver(deezer, locator, new MemoryCache(new MemoryCacheOptions()), TimeProvider.System,
            NullLogger<CatalogAudioResolver>.Instance);
        return (resolver, locator);
    }

    [Fact]
    public async Task ResolvesFromTheDeezerTracksArtistAndTitle()
    {
        DownloadRequestSeen? seen = null;
        var locator = new FakeLocator(request =>
        {
            seen = new DownloadRequestSeen(request.Artist, request.Title, request.Album, request.Isrc, request.DurationMs);
            return CatalogTestData.Audio();
        });
        var (resolver, _) = Build(locator);

        var audio = await resolver.ResolveAsync("42", default);

        Assert.Equal("vid123", audio.YouTubeVideoId);
        Assert.Equal(new DownloadRequestSeen("Daft Punk", "Harder", "Discovery", "GBDUW0000059", 224_000), seen);
    }

    [Fact]
    public async Task AResolvedStream_IsReusedWhileItsUrlLives()
    {
        var (resolver, locator) = Build();

        var first = await resolver.ResolveAsync("42", default);
        var second = await resolver.ResolveAsync("42", default);

        Assert.Same(first, second);
        Assert.Equal(1, locator.Calls);
    }

    [Fact]
    public async Task AUrlAboutToLapse_IsNotKept()
    {
        var soon = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds();
        var (resolver, locator) = Build(new FakeLocator(_ => CatalogTestData.Audio(expire: soon)));

        await resolver.ResolveAsync("42", default);
        await resolver.ResolveAsync("42", default);

        Assert.Equal(2, locator.Calls);
    }

    [Fact]
    public async Task InvalidatingAnId_MakesTheNextCallResolveAgain()
    {
        var (resolver, locator) = Build();

        var stale = await resolver.ResolveAsync("42", default);
        resolver.Invalidate("42", stale.StreamUrl);
        await resolver.ResolveAsync("42", default);

        Assert.Equal(2, locator.Calls);
    }

    [Fact]
    public async Task InvalidatingAnOlderUrl_KeepsTheNewerStream()
    {
        var (resolver, locator) = Build();

        await resolver.ResolveAsync("42", default);
        resolver.Invalidate("42", "https://rr9.googlevideo.com/videoplayback?id=old");
        await resolver.ResolveAsync("42", default);

        Assert.Equal(1, locator.Calls);
    }

    [Theory]
    [InlineData("rate-limited")]
    [InlineData("unreachable")]
    [InlineData("timed-out")]
    public async Task DeezerFailingTheLookup_IsReportedAsCatalogUnavailable(string how)
    {
        Exception failure = how switch
        {
            "rate-limited" => new ProviderRateLimitedException(TimeSpan.FromSeconds(5)),
            "timed-out" => new TaskCanceledException("HttpClient.Timeout elapsed", new TimeoutException()),
            _ => new HttpRequestException("connection refused"),
        };
        var (resolver, locator) = Build(lookupFailure: failure);

        var ex = await Assert.ThrowsAsync<CatalogResolveException>(() => resolver.ResolveAsync("42", default));

        Assert.Equal(CatalogResolveFailure.CatalogUnavailable, ex.Kind);
        Assert.Equal(0, locator.Calls);
        var result = Assert.IsAssignableFrom<IStatusCodeHttpResult>(CatalogEndpoints.ResolveFailure(ex));
        Assert.Equal(StatusCodes.Status502BadGateway, result.StatusCode);
    }

    [Fact]
    public async Task AtMostTwoYtDlpRuns_GoAtOnce()
    {
        var gate = new TaskCompletionSource();
        var running = 0;
        var peak = 0;
        var locator = new FakeLocator(_ =>
        {
            peak = Math.Max(peak, Interlocked.Increment(ref running));
            gate.Task.Wait();
            Interlocked.Decrement(ref running);
            return CatalogTestData.Audio();
        });
        var deezer = new FakeDeezerCatalogService();
        foreach (var id in new[] { "1", "2", "3", "4" })
            deezer.TracksById[id] = new DeezerCatalogTrack(id, "Song " + id, "Artist", "Album", 2001, 1, 200_000, null);
        var resolver = new CatalogAudioResolver(deezer, locator, new MemoryCache(new MemoryCacheOptions()), TimeProvider.System,
            NullLogger<CatalogAudioResolver>.Instance);

        var calls = new[] { "1", "2", "3", "4" }.Select(id => Task.Run(() => resolver.ResolveAsync(id, default))).ToList();
        await Task.Delay(300);
        var peakWhileBlocked = peak;
        gate.SetResult();
        await Task.WhenAll(calls);

        Assert.Equal(2, peakWhileBlocked);
        Assert.Equal(4, locator.Calls);
    }

    [Fact]
    public async Task ConcurrentCallsForOneId_ShareASingleResolve()
    {
        var gate = new TaskCompletionSource();
        var (resolver, locator) = Build(new FakeLocator(_ => CatalogTestData.Audio()) { Gate = gate });

        var calls = Enumerable.Range(0, 5).Select(_ => resolver.ResolveAsync("42", default)).ToList();
        gate.SetResult();
        var results = await Task.WhenAll(calls);

        Assert.Equal(1, locator.Calls);
        Assert.All(results, r => Assert.Same(results[0], r));
    }

    [Fact]
    public async Task ACallerThatCancels_DoesNotCancelTheSharedResolve()
    {
        var gate = new TaskCompletionSource();
        var (resolver, locator) = Build(new FakeLocator(_ => CatalogTestData.Audio()) { Gate = gate });
        using var cts = new CancellationTokenSource();

        var leaving = resolver.ResolveAsync("42", cts.Token);
        var staying = resolver.ResolveAsync("42", default);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaving);
        gate.SetResult();

        Assert.Equal("vid123", (await staying).YouTubeVideoId);
        Assert.Equal(1, locator.Calls);
    }

    [Fact]
    public async Task AFailedResolve_IsNotCached_AndTheNextCallTriesAgain()
    {
        var attempt = 0;
        var (resolver, locator) = Build(new FakeLocator(_ =>
            ++attempt == 1
                ? throw new CatalogResolveException(CatalogResolveFailure.YtDlpFailed, "boom")
                : CatalogTestData.Audio()));

        await Assert.ThrowsAsync<CatalogResolveException>(() => resolver.ResolveAsync("42", default));
        var audio = await resolver.ResolveAsync("42", default);

        Assert.Equal("vid123", audio.YouTubeVideoId);
        Assert.Equal(2, locator.Calls);
    }

    [Fact]
    public async Task ATrackDeezerDoesNotKnow_IsNotFound_WithoutAskingYouTube()
    {
        var (resolver, locator) = Build(knownTrack: false);

        var ex = await Assert.ThrowsAsync<CatalogResolveException>(() => resolver.ResolveAsync("42", default));

        Assert.Equal(CatalogResolveFailure.NotFound, ex.Kind);
        Assert.Equal(0, locator.Calls);
    }

    [Fact]
    public async Task TheResolveEndpoint_ReturnsTheVideoIdTitleAndDuration_NotTheStreamUrl()
    {
        var (resolver, _) = Build();

        var result = await CatalogEndpoints.ResolveFull("42", resolver, default);

        var ok = Assert.IsType<Ok<FullTrackResolveResponse>>(result);
        Assert.Equal(new FullTrackResolveResponse("vid123", "Artist - Title (Official Video)", 223_000), ok.Value);
    }

    [Theory]
    [InlineData(CatalogResolveFailure.NotFound, 404, "not_found")]
    [InlineData(CatalogResolveFailure.YtDlpFailed, 422, "youtube_failed")]
    [InlineData(CatalogResolveFailure.YtDlpMissing, 503, "youtube_unavailable")]
    public async Task TheResolveEndpoint_MapsEachFailureToItsStatusAndErrorCode(CatalogResolveFailure kind, int status, string error)
    {
        var (resolver, _) = Build(new FakeLocator(_ => throw new CatalogResolveException(kind, "the message")));

        var result = await CatalogEndpoints.ResolveFull("42", resolver, default);

        Assert.Equal(status, ((IStatusCodeHttpResult)result).StatusCode);
        var value = ((IValueHttpResult)result).Value!;
        Assert.Equal(error, value.GetType().GetProperty("error")!.GetValue(value));
        Assert.Equal("the message", value.GetType().GetProperty("message")!.GetValue(value));
    }

    [Fact]
    public async Task TheResolveEndpoint_Answers400_ForANonNumericId()
    {
        var (resolver, locator) = Build();

        var result = await CatalogEndpoints.ResolveFull("x1", resolver, default);

        Assert.Equal(StatusCodes.Status400BadRequest, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Equal(0, locator.Calls);
    }

    private sealed record DownloadRequestSeen(string Artist, string Title, string? Album, string? Isrc, int DurationMs);
}
