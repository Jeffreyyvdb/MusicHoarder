using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Tests.Spotify;

/// <summary>
/// Pins the app-token transport behind the catalog lookups, observed through the public
/// service surface: how a client-credentials token is requested and cached, and what a 429,
/// a 401 and a plain failure from the Web API do to the call. Both the track-search path
/// and the generic lookup path (via <c>GetTrackAsync</c>) are exercised so that the two
/// have to agree.
/// </summary>
public class SpotifyCatalogSearchTransportTests
{
    private const string SearchJson =
        """{"tracks":{"items":[{"id":"track1","name":"Song","duration_ms":1000,"artists":[{"name":"A"}],"album":{"name":"Al","release_date":"2020"}}]}}""";

    private const string TrackJson =
        """{"id":"track1","name":"Song","duration_ms":1000,"artists":[{"name":"A"}],"album":{"name":"Al","release_date":"2020"}}""";

    // ── token acquisition ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchTracksAsync_RequestsTheTokenWithBasicAuthAndTheClientCredentialsGrant()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueToken("tok");
        handler.EnqueueJson(SearchJson);

        _ = await CreateService(handler).SearchTracksAsync("cid", "sec", "q", CancellationToken.None);

        var tokenRequest = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, tokenRequest.Method);
        Assert.Equal("https://accounts.spotify.com/api/token", tokenRequest.Url);
        Assert.Equal("Basic", tokenRequest.AuthScheme);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("cid:sec")), tokenRequest.AuthParameter);
        Assert.Equal("grant_type=client_credentials", tokenRequest.Body);

        var apiRequest = handler.Requests[1];
        Assert.Equal(HttpMethod.Get, apiRequest.Method);
        Assert.StartsWith("https://api.spotify.com/v1/search?", apiRequest.Url);
        Assert.Equal("Bearer", apiRequest.AuthScheme);
        Assert.Equal("tok", apiRequest.AuthParameter);
        Assert.Contains("MusicHoarder/1.0", apiRequest.UserAgent);
    }

    [Fact]
    public async Task SearchTracksAsync_TokenRequestFails_ReturnsEmptyWithoutCallingTheApi()
    {
        var handler = new ScriptedHttpHandler();
        handler.Enqueue(HttpStatusCode.BadRequest, """{"error":"invalid_client"}""");

        var tracks = await CreateService(handler).SearchTracksAsync("cid", "sec", "q", CancellationToken.None);

        Assert.Empty(tracks);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task SearchTracksAsync_CachesTheTokenPerClientId()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueToken("tok-one");
        handler.EnqueueJson(SearchJson);
        handler.EnqueueToken("tok-two");
        handler.EnqueueJson(SearchJson);
        handler.EnqueueJson(SearchJson);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(handler, cache);

        _ = await service.SearchTracksAsync("client-one", "s1", "query a", CancellationToken.None);
        _ = await service.SearchTracksAsync("client-two", "s2", "query b", CancellationToken.None);
        Assert.Equal(4, handler.SendCount);

        // A third call for the first client (new query, so no search-cache hit) needs no new token.
        _ = await service.SearchTracksAsync("client-one", "s1", "query c", CancellationToken.None);
        Assert.Equal(5, handler.SendCount);
        Assert.Equal("tok-one", handler.Requests[4].AuthParameter);
    }

    // ── 429 ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchTracksAsync_RateLimited_RetriesAfterTheRetryAfterHeader()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueToken();
        handler.Enqueue(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.Zero);
        handler.EnqueueJson(SearchJson);

        var tracks = await CreateService(handler).SearchTracksAsync("cid", "sec", "q", CancellationToken.None);

        Assert.Single(tracks);
        Assert.Equal("track1", tracks[0].Id);
        Assert.Equal(3, handler.SendCount);
    }

    [Fact]
    public async Task SearchTracksAsync_RateLimitedOnEveryAttempt_ThrowsProviderRateLimited()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueToken();
        for (var i = 0; i < 4; i++)
            handler.Enqueue(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.Zero);

        var ex = await Assert.ThrowsAsync<ProviderRateLimitedException>(
            () => CreateService(handler).SearchTracksAsync("cid", "sec", "q", CancellationToken.None));

        // Four attempts (one initial + three retries), the header's delay carried on the exception.
        Assert.Equal(5, handler.SendCount);
        Assert.Equal(TimeSpan.Zero, ex.RetryAfter);
    }

    [Fact]
    public async Task GetTrackAsync_RateLimited_RetriesAfterTheRetryAfterHeader()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueToken();
        handler.Enqueue(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.Zero);
        handler.EnqueueJson(TrackJson);

        var track = await CreateService(handler).GetTrackAsync("cid", "sec", "track1", CancellationToken.None);

        Assert.NotNull(track);
        Assert.Equal("track1", track!.Id);
        Assert.Equal(3, handler.SendCount);
    }

    [Fact]
    public async Task GetTrackAsync_RateLimitedOnEveryAttempt_ThrowsProviderRateLimited()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueToken();
        for (var i = 0; i < 4; i++)
            handler.Enqueue(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.Zero);

        await Assert.ThrowsAsync<ProviderRateLimitedException>(
            () => CreateService(handler).GetTrackAsync("cid", "sec", "track1", CancellationToken.None));

        Assert.Equal(5, handler.SendCount);
    }

    // ── 401 ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchTracksAsync_Unauthorized_FetchesAFreshTokenAndRetriesOnce()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueToken("stale");
        handler.Enqueue(HttpStatusCode.Unauthorized);
        handler.EnqueueToken("fresh");
        handler.EnqueueJson(SearchJson);

        var tracks = await CreateService(handler).SearchTracksAsync("cid", "sec", "q", CancellationToken.None);

        Assert.Single(tracks);
        Assert.Equal(4, handler.SendCount);
        Assert.Equal("stale", handler.Requests[1].AuthParameter);
        Assert.Equal(HttpMethod.Post, handler.Requests[2].Method);
        Assert.Equal("fresh", handler.Requests[3].AuthParameter);
    }

    [Fact]
    public async Task SearchTracksAsync_UnauthorizedAgainAfterTheRefresh_ReturnsEmpty()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueToken("stale");
        handler.Enqueue(HttpStatusCode.Unauthorized);
        handler.EnqueueToken("fresh");
        handler.Enqueue(HttpStatusCode.Unauthorized);

        var tracks = await CreateService(handler).SearchTracksAsync("cid", "sec", "q", CancellationToken.None);

        Assert.Empty(tracks);
        Assert.Equal(4, handler.SendCount);
    }

    [Fact]
    public async Task GetTrackAsync_Unauthorized_FetchesAFreshTokenAndRetriesOnce()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueToken("stale");
        handler.Enqueue(HttpStatusCode.Unauthorized);
        handler.EnqueueToken("fresh");
        handler.EnqueueJson(TrackJson);

        var track = await CreateService(handler).GetTrackAsync("cid", "sec", "track1", CancellationToken.None);

        Assert.NotNull(track);
        Assert.Equal(4, handler.SendCount);
        Assert.Equal("stale", handler.Requests[1].AuthParameter);
        Assert.Equal("fresh", handler.Requests[3].AuthParameter);
    }

    [Fact]
    public async Task GetTrackAsync_UnauthorizedAgainAfterTheRefresh_ReturnsNull()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueToken("stale");
        handler.Enqueue(HttpStatusCode.Unauthorized);
        handler.EnqueueToken("fresh");
        handler.Enqueue(HttpStatusCode.Unauthorized);

        var track = await CreateService(handler).GetTrackAsync("cid", "sec", "track1", CancellationToken.None);

        Assert.Null(track);
        Assert.Equal(4, handler.SendCount);
    }

    [Fact]
    public async Task Unauthorized_ReplacesTheCachedToken_ForTheNextCall()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueToken("stale");
        handler.Enqueue(HttpStatusCode.Unauthorized);
        handler.EnqueueToken("fresh");
        handler.EnqueueJson(TrackJson);
        handler.EnqueueJson(TrackJson);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(handler, cache);

        _ = await service.GetTrackAsync("cid", "sec", "track1", CancellationToken.None);
        _ = await service.GetTrackAsync("cid", "sec", "track2", CancellationToken.None);

        Assert.Equal(5, handler.SendCount);
        Assert.Equal(HttpMethod.Get, handler.Requests[4].Method);
        Assert.Equal("fresh", handler.Requests[4].AuthParameter);
    }

    // ── other failures ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchTracksAsync_ServerError_ReturnsEmptyWithoutRetrying()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueToken();
        handler.Enqueue(HttpStatusCode.InternalServerError, "boom");

        var tracks = await CreateService(handler).SearchTracksAsync("cid", "sec", "q", CancellationToken.None);

        Assert.Empty(tracks);
        Assert.Equal(2, handler.SendCount);
    }

    [Fact]
    public async Task GetTrackAsync_NotFound_ReturnsNullWithoutRetrying()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueToken();
        handler.Enqueue(HttpStatusCode.NotFound, """{"error":{"status":404}}""");

        var track = await CreateService(handler).GetTrackAsync("cid", "sec", "missing", CancellationToken.None);

        Assert.Null(track);
        Assert.Equal(2, handler.SendCount);
    }

    [Fact]
    public async Task GetTrackAsync_EmptyCredentials_ReturnsNullWithoutHttp()
    {
        var handler = new ScriptedHttpHandler();

        var track = await CreateService(handler).GetTrackAsync("", "", "track1", CancellationToken.None);

        Assert.Null(track);
        Assert.Equal(0, handler.SendCount);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────

    private static SpotifyCatalogSearchService CreateService(ScriptedHttpHandler handler, IMemoryCache? cache = null)
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
        var api = new SpotifyClientCredentialsClient(httpClient, cache, opts, NullLogger<SpotifyClientCredentialsClient>.Instance);
        return new SpotifyCatalogSearchService(api, cache, opts);
    }
}
