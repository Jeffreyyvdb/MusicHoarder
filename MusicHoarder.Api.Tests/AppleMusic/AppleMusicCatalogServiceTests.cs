using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.AppleMusic;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.AppleMusic;

public class AppleMusicCatalogServiceTests
{
    public AppleMusicCatalogServiceTests() => AppleMusicCatalogService.ResetBackoffForTests();

    [Fact]
    public async Task SearchTracksAsync_RateLimited_RetriesUpToMaxThenThrows()
    {
        var handler = new FakeHttpHandler();
        // Always 403 with a short Retry-After so the in-process retry doesn't slow the test.
        handler.AlwaysRespond(() => RateLimited(HttpStatusCode.Forbidden, TimeSpan.FromMilliseconds(1)));

        var service = CreateService(handler);

        await Assert.ThrowsAsync<ProviderRateLimitedException>(
            () => service.SearchTracksAsync("artist title", CancellationToken.None));

        // MaxRetries = 2 → exactly 2 HTTP attempts (no off-by-one third call).
        Assert.Equal(2, handler.SendCount);
    }

    [Fact]
    public async Task SearchTracksAsync_AfterRateLimit_ShortCircuitsWithoutHttp()
    {
        var handler = new FakeHttpHandler();
        // Long Retry-After so the backoff window is still open for the immediate second call.
        handler.AlwaysRespond(() => RateLimited(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(30)));

        var service = CreateService(handler);

        await Assert.ThrowsAsync<ProviderRateLimitedException>(
            () => service.SearchTracksAsync("artist title", CancellationToken.None));
        var countAfterFirst = handler.SendCount;

        // A different query (so it can't be served from cache) must still bail out without HTTP.
        await Assert.ThrowsAsync<ProviderRateLimitedException>(
            () => service.SearchTracksAsync("another query", CancellationToken.None));

        Assert.Equal(countAfterFirst, handler.SendCount);
    }

    private static HttpResponseMessage RateLimited(HttpStatusCode status, TimeSpan retryAfter)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
        return response;
    }

    private static AppleMusicCatalogService CreateService(FakeHttpHandler handler)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var opts = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/s",
            DestinationDirectory = "/d",
            AppleMusicApiRequestsPerMinute = 60,
            AppleMusicApiSearchLimit = 10,
            AppleMusicApiSearchCacheMinutes = 60,
        });
        return new AppleMusicCatalogService(httpClient, cache, opts, NullLogger<AppleMusicCatalogService>.Instance);
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private Func<HttpResponseMessage>? _factory;

        public int SendCount { get; private set; }

        public void AlwaysRespond(Func<HttpResponseMessage> factory) => _factory = factory;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(_factory!());
        }
    }
}
