using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Spotify;
using MusicHoarder.Api.Tests.Playback;

namespace MusicHoarder.Api.Tests.Spotify;

/// <summary>
/// The app-token transport in isolation: no catalog parsing, just a URL in and a body (or null)
/// out, with the token cache, the 429/401 policy and the failure cases asserted directly.
/// </summary>
public class SpotifyClientCredentialsClientTests
{
    private const string Url = "https://api.spotify.com/v1/tracks/abc";

    // ── credentials and token acquisition ───────────────────────────────────────────────

    [Theory]
    [InlineData("", "sec")]
    [InlineData("cid", "")]
    [InlineData("  ", "  ")]
    public async Task GetJsonAsync_BlankCredentials_ReturnsNullWithoutHttp(string clientId, string clientSecret)
    {
        var handler = new ScriptedHttpHandler();

        var json = await CreateClient(handler).GetJsonAsync(clientId, clientSecret, Url, CancellationToken.None);

        Assert.Null(json);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task GetJsonAsync_ReturnsTheBody_SentWithTheBearerTokenAndUserAgent()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueToken("tok");
        handler.EnqueueJson("""{"id":"abc"}""");

        var json = await CreateClient(handler).GetJsonAsync("cid", "sec", Url, CancellationToken.None);

        Assert.Equal("""{"id":"abc"}""", json);
        var get = handler.Requests[1];
        Assert.Equal(HttpMethod.Get, get.Method);
        Assert.Equal(Url, get.Url);
        Assert.Equal("Bearer", get.AuthScheme);
        Assert.Equal("tok", get.AuthParameter);
        Assert.Contains("MusicHoarder/1.0", get.UserAgent);
    }

    [Fact]
    public async Task GetJsonAsync_RequestsTheTokenWithBasicAuthAndTheClientCredentialsGrant()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueToken();
        handler.EnqueueJson("{}");

        _ = await CreateClient(handler).GetJsonAsync("cid", "sec", Url, CancellationToken.None);

        var token = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, token.Method);
        Assert.Equal("https://accounts.spotify.com/api/token", token.Url);
        Assert.Equal("Basic", token.AuthScheme);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("cid:sec")), token.AuthParameter);
        Assert.Equal("grant_type=client_credentials", token.Body);
        Assert.Contains("MusicHoarder/1.0", token.UserAgent);
    }

    [Fact]
    public async Task GetJsonAsync_ReusesTheCachedToken_AcrossCalls()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueToken("tok");
        handler.EnqueueJson("{}");
        handler.EnqueueJson("{}");

        var client = CreateClient(handler);
        _ = await client.GetJsonAsync("cid", "sec", Url, CancellationToken.None);
        _ = await client.GetJsonAsync("cid", "sec", Url + "2", CancellationToken.None);

        Assert.Equal(3, handler.SendCount);
        Assert.Equal(HttpMethod.Get, handler.Requests[2].Method);
        Assert.Equal("tok", handler.Requests[2].AuthParameter);
    }

    [Fact]
    public async Task GetJsonAsync_CachesTokensPerClientId()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueToken("tok-one");
        handler.EnqueueJson("{}");
        handler.EnqueueToken("tok-two");
        handler.EnqueueJson("{}");
        handler.EnqueueJson("{}");

        var client = CreateClient(handler);
        _ = await client.GetJsonAsync("client-one", "s1", Url, CancellationToken.None);
        _ = await client.GetJsonAsync("client-two", "s2", Url, CancellationToken.None);
        _ = await client.GetJsonAsync("client-one", "s1", Url, CancellationToken.None);

        Assert.Equal(5, handler.SendCount);
        Assert.Equal("tok-one", handler.Requests[1].AuthParameter);
        Assert.Equal("tok-two", handler.Requests[3].AuthParameter);
        Assert.Equal("tok-one", handler.Requests[4].AuthParameter);
    }

    [Fact]
    public async Task GetJsonAsync_ConcurrentCallersForOneClientId_ShareASingleTokenRequest()
    {
        var handler = new GatedTokenHandler();
        var client = CreateClient(handler);

        var first = client.GetJsonAsync("cid", "sec", Url, CancellationToken.None);
        await handler.TokenRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = client.GetJsonAsync("cid", "sec", Url + "2", CancellationToken.None);

        // Let the second caller reach the token lock before the first token lands.
        await Task.Delay(50);
        handler.ReleaseToken.SetResult();

        Assert.Equal("{}", await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("{}", await second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, handler.TokenRequests);
        Assert.Equal(2, handler.ApiRequests);
    }

    [Fact]
    public async Task GetJsonAsync_TokenRequestFails_ReturnsNullWithoutCallingTheApi()
    {
        var handler = new ScriptedHttpHandler();
        handler.Enqueue(HttpStatusCode.BadRequest, """{"error":"invalid_client"}""");

        var json = await CreateClient(handler).GetJsonAsync("cid", "sec", Url, CancellationToken.None);

        Assert.Null(json);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task GetJsonAsync_EmptyAccessToken_ReturnsNullWithoutCallingTheApi()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueJson("""{"access_token":"","expires_in":3600}""");

        var json = await CreateClient(handler).GetJsonAsync("cid", "sec", Url, CancellationToken.None);

        Assert.Null(json);
        Assert.Equal(1, handler.SendCount);
    }

    // ── token lifetime ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetJsonAsync_DropsTheToken90SecondsBeforeSpotifyExpiresIt()
    {
        var clock = new ManualClock();
        using var cache = new MemoryCache(new MemoryCacheOptions { Clock = clock });
        var handler = new ScriptedHttpHandler();
        handler.EnqueueToken("first", expiresIn: 3600);
        handler.EnqueueJson("{}");
        handler.EnqueueJson("{}");
        handler.EnqueueToken("second", expiresIn: 3600);
        handler.EnqueueJson("{}");

        var client = CreateClient(handler, cache);
        _ = await client.GetJsonAsync("cid", "sec", Url, CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(3500));
        _ = await client.GetJsonAsync("cid", "sec", Url, CancellationToken.None);
        Assert.Equal("first", handler.Requests[^1].AuthParameter);

        clock.Advance(TimeSpan.FromSeconds(20));
        _ = await client.GetJsonAsync("cid", "sec", Url, CancellationToken.None);
        Assert.Equal(HttpMethod.Post, handler.Requests[^2].Method);
        Assert.Equal("second", handler.Requests[^1].AuthParameter);
    }

    [Fact]
    public async Task GetJsonAsync_KeepsAShortLivedTokenForAtLeastTwoMinutes()
    {
        var clock = new ManualClock();
        using var cache = new MemoryCache(new MemoryCacheOptions { Clock = clock });
        var handler = new ScriptedHttpHandler();
        handler.EnqueueToken("first", expiresIn: 60);
        handler.EnqueueJson("{}");
        handler.EnqueueJson("{}");
        handler.EnqueueToken("second", expiresIn: 60);
        handler.EnqueueJson("{}");

        var client = CreateClient(handler, cache);
        _ = await client.GetJsonAsync("cid", "sec", Url, CancellationToken.None);

        // expires_in - 90 would be negative; the floor keeps the token for 120 s instead.
        clock.Advance(TimeSpan.FromSeconds(100));
        _ = await client.GetJsonAsync("cid", "sec", Url, CancellationToken.None);
        Assert.Equal("first", handler.Requests[^1].AuthParameter);

        clock.Advance(TimeSpan.FromSeconds(30));
        _ = await client.GetJsonAsync("cid", "sec", Url, CancellationToken.None);
        Assert.Equal("second", handler.Requests[^1].AuthParameter);
    }

    // ── 429 ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetJsonAsync_RateLimited_WaitsOutRetryAfterAndRetries()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueToken();
        handler.Enqueue(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.Zero);
        handler.Enqueue(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.Zero);
        handler.EnqueueJson("""{"ok":true}""");

        var json = await CreateClient(handler).GetJsonAsync("cid", "sec", Url, CancellationToken.None);

        Assert.Equal("""{"ok":true}""", json);
        Assert.Equal(4, handler.SendCount);
    }

    [Fact]
    public async Task GetJsonAsync_RateLimitedOnEveryAttempt_ThrowsWithTheHeaderDelay()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueToken();
        for (var i = 0; i < 4; i++)
            handler.Enqueue(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.Zero);

        var ex = await Assert.ThrowsAsync<ProviderRateLimitedException>(
            () => CreateClient(handler).GetJsonAsync("cid", "sec", Url, CancellationToken.None));

        Assert.Equal(TimeSpan.Zero, ex.RetryAfter);
        Assert.Equal(5, handler.SendCount);
    }

    // Issue #550: Spotify's Development Mode quota answers 429 QUOTA_EXCEEDED with a Retry-After of
    // hours. Waiting that out in place parked every enrichment worker while the app looked healthy.
    private static readonly TimeSpan QuotaRetryAfter = TimeSpan.FromSeconds(76242);
    private const string AlbumUrl = "https://api.spotify.com/v1/albums/alb1";
    private const string SearchUrl = "https://api.spotify.com/v1/search?q=x&type=track";

    [Fact]
    public async Task GetJsonAsync_RetryAfterLongerThanTheInlineCap_ThrowsAtOnceWithoutWaiting()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueToken();
        handler.Enqueue(HttpStatusCode.TooManyRequests, """{"error":{"status":429,"reason":"QUOTA_EXCEEDED"}}""", QuotaRetryAfter);

        var call = CreateClient(handler).GetJsonAsync("cid", "sec", AlbumUrl, CancellationToken.None);
        var finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(call, finished);
        var ex = await Assert.ThrowsAsync<ProviderRateLimitedException>(() => call);
        Assert.Equal(QuotaRetryAfter, ex.RetryAfter);
        Assert.Equal(2, handler.SendCount); // token + the one 429, no retries
    }

    [Fact]
    public async Task GetJsonAsync_AfterALongRetryAfter_SkipsThatEndpointWithoutHttpUntilItExpires()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        var handler = new ScriptedHttpHandler();
        handler.EnqueueToken();
        handler.Enqueue(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromHours(2));

        var client = CreateClient(handler, timeProvider: clock);
        await Assert.ThrowsAsync<ProviderRateLimitedException>(
            () => client.GetJsonAsync("cid", "sec", AlbumUrl, CancellationToken.None));

        clock.Advance(TimeSpan.FromMinutes(30));
        var ex = await Assert.ThrowsAsync<ProviderRateLimitedException>(
            () => client.GetJsonAsync("cid", "sec", "https://api.spotify.com/v1/albums/other", CancellationToken.None));
        Assert.Equal(TimeSpan.FromMinutes(90), ex.RetryAfter);
        Assert.Equal(2, handler.SendCount);

        clock.Advance(TimeSpan.FromMinutes(91));
        handler.EnqueueJson("""{"id":"alb1"}""");
        Assert.Equal("""{"id":"alb1"}""", await client.GetJsonAsync("cid", "sec", AlbumUrl, CancellationToken.None));
    }

    [Fact]
    public async Task GetJsonAsync_ABlockedEndpoint_DoesNotBlockTheOthers()
    {
        // The reporter's direct check: /v1/albums was closed for 21 h while /v1/search still answered.
        var handler = new ScriptedHttpHandler();
        handler.EnqueueToken();
        handler.Enqueue(HttpStatusCode.TooManyRequests, retryAfter: QuotaRetryAfter);
        handler.EnqueueJson("""{"tracks":{"items":[]}}""");

        var client = CreateClient(handler);
        await Assert.ThrowsAsync<ProviderRateLimitedException>(
            () => client.GetJsonAsync("cid", "sec", AlbumUrl, CancellationToken.None));

        Assert.Equal("""{"tracks":{"items":[]}}""", await client.GetJsonAsync("cid", "sec", SearchUrl, CancellationToken.None));
    }

    [Fact]
    public async Task GetJsonAsync_RetryAfterAtTheInlineCap_IsStillWaitedOut()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueToken();
        handler.Enqueue(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.Zero);
        handler.EnqueueJson("""{"ok":true}""");

        var json = await CreateClient(handler, maxInlineRetryAfterSeconds: 0)
            .GetJsonAsync("cid", "sec", AlbumUrl, CancellationToken.None);

        Assert.Equal("""{"ok":true}""", json);
    }

    [Theory]
    [InlineData("https://api.spotify.com/v1/search?q=a", "search")]
    [InlineData("https://api.spotify.com/v1/albums/abc", "albums")]
    [InlineData("https://api.spotify.com/v1/tracks/abc", "tracks")]
    [InlineData("https://api.spotify.com/v1/artists/abc/albums", "artists")]
    public void EndpointOf_IsTheResourceAfterV1(string url, string expected) =>
        Assert.Equal(expected, SpotifyClientCredentialsClient.EndpointOf(url));

    // ── 401 ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetJsonAsync_Unauthorized_RefreshesTheTokenOnceAndRetries()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueToken("stale");
        handler.Enqueue(HttpStatusCode.Unauthorized);
        handler.EnqueueToken("fresh");
        handler.EnqueueJson("""{"ok":true}""");

        var json = await CreateClient(handler).GetJsonAsync("cid", "sec", Url, CancellationToken.None);

        Assert.Equal("""{"ok":true}""", json);
        Assert.Equal(4, handler.SendCount);
        Assert.Equal("stale", handler.Requests[1].AuthParameter);
        Assert.Equal(HttpMethod.Post, handler.Requests[2].Method);
        Assert.Equal("fresh", handler.Requests[3].AuthParameter);
    }

    [Fact]
    public async Task GetJsonAsync_Unauthorized_ReplacesTheCachedTokenForLaterCalls()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueToken("stale");
        handler.Enqueue(HttpStatusCode.Unauthorized);
        handler.EnqueueToken("fresh");
        handler.EnqueueJson("{}");
        handler.EnqueueJson("{}");

        var client = CreateClient(handler);
        _ = await client.GetJsonAsync("cid", "sec", Url, CancellationToken.None);
        _ = await client.GetJsonAsync("cid", "sec", Url + "2", CancellationToken.None);

        Assert.Equal(5, handler.SendCount);
        Assert.Equal(HttpMethod.Get, handler.Requests[4].Method);
        Assert.Equal("fresh", handler.Requests[4].AuthParameter);
    }

    [Fact]
    public async Task GetJsonAsync_UnauthorizedAgainAfterTheRefresh_ReturnsNull()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueToken("stale");
        handler.Enqueue(HttpStatusCode.Unauthorized);
        handler.EnqueueToken("fresh");
        handler.Enqueue(HttpStatusCode.Unauthorized);

        var json = await CreateClient(handler).GetJsonAsync("cid", "sec", Url, CancellationToken.None);

        Assert.Null(json);
        Assert.Equal(4, handler.SendCount);
    }

    [Fact]
    public async Task GetJsonAsync_RefreshAfterUnauthorizedFails_ReturnsNull()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueToken("stale");
        handler.Enqueue(HttpStatusCode.Unauthorized);
        handler.Enqueue(HttpStatusCode.InternalServerError, "accounts down");

        var json = await CreateClient(handler).GetJsonAsync("cid", "sec", Url, CancellationToken.None);

        Assert.Null(json);
        Assert.Equal(3, handler.SendCount);
    }

    // ── other failures ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task GetJsonAsync_OtherNonSuccessStatus_ReturnsNullWithoutRetrying(HttpStatusCode status)
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueToken();
        handler.Enqueue(status, """{"error":{"message":"nope"}}""");

        var json = await CreateClient(handler).GetJsonAsync("cid", "sec", Url, CancellationToken.None);

        Assert.Null(json);
        Assert.Equal(2, handler.SendCount);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────

    private static SpotifyClientCredentialsClient CreateClient(
        HttpMessageHandler handler,
        IMemoryCache? cache = null,
        TimeProvider? timeProvider = null,
        int maxInlineRetryAfterSeconds = 30)
    {
        cache ??= new MemoryCache(new MemoryCacheOptions());
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var opts = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/s",
            DestinationDirectory = "/d",
            SpotifyApiRequestsPerSecond = 20,
            SpotifyApiMaxInlineRetryAfterSeconds = maxInlineRetryAfterSeconds,
        });
        return new SpotifyClientCredentialsClient(
            httpClient, cache, opts, NullLogger<SpotifyClientCredentialsClient>.Instance, timeProvider ?? TimeProvider.System);
    }

    private sealed class ManualClock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    /// <summary>
    /// Holds the token response until released, so two callers can be parked on the token lock
    /// at once; every GET answers immediately with <c>{}</c>.
    /// </summary>
    private sealed class GatedTokenHandler : HttpMessageHandler
    {
        private int _tokenRequests;
        private int _apiRequests;

        public TaskCompletionSource TokenRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseToken { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int TokenRequests => Volatile.Read(ref _tokenRequests);
        public int ApiRequests => Volatile.Read(ref _apiRequests);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                Interlocked.Increment(ref _tokenRequests);
                TokenRequested.TrySetResult();
                await ReleaseToken.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"access_token":"tok","expires_in":3600}""", Encoding.UTF8, "application/json")
                };
            }

            Interlocked.Increment(ref _apiRequests);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }
    }
}
