using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.RateLimiting;

namespace MusicHoarder.Api.Spotify;

/// <summary>
/// App-authenticated GET against the Spotify Web API for the catalog lookups that need no user:
/// a client-credentials token per app id (fetched under a lock, cached until shortly before it
/// expires), the process-wide request rate limit, and the response policy every lookup shares —
/// a 429 with a short <c>Retry-After</c> is waited out and retried, a long one (a Development Mode
/// quota answers with hours) is reported at once and the endpoint skipped until it expires, a 401
/// refreshes the token exactly once, and anything else non-2xx gives up. Callers hand it a URL and
/// get the JSON body back, or <c>null</c> when Spotify had nothing usable; parsing is theirs
/// (<see cref="SpotifyCatalogSearchService"/>). User-token requests do not come through here —
/// <see cref="SpotifyApiService"/> refreshes the owner's OAuth grant instead.
/// </summary>
public sealed class SpotifyClientCredentialsClient(
    HttpClient httpClient,
    IMemoryCache cache,
    IOptions<MusicEnricherOptions> options,
    ILogger<SpotifyClientCredentialsClient> logger,
    TimeProvider time)
{
    private const string AccountsTokenUrl = "https://accounts.spotify.com/api/token";
    private const string UserAgent = "MusicHoarder/1.0 (https://github.com/Jeffreyyvdb/MusicHoarder)";
    private static readonly TimeSpan RateLimitDefaultDelay = TimeSpan.FromSeconds(5);
    private const int MaxRetries = 3;

    // One bucket per process, like every other catalog client's, so every consumer of the
    // catalog service shares the configured SpotifyApiRequestsPerSecond.
    private static readonly ReconfigurableRateLimiter RateLimiter = new();

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _tokenLocks = new(StringComparer.Ordinal);

    // Endpoint ("search", "albums", …) → when Spotify said it may be asked again, after a
    // Retry-After too long to wait out. Per endpoint: a quota can close one while another answers.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _endpointBlockedUntil = new(StringComparer.Ordinal);

    /// <summary>
    /// GETs <paramref name="url"/> as the app identified by <paramref name="clientId"/> and returns the
    /// response body, or <c>null</c> when the credentials are blank, no token could be obtained, the
    /// rate limiter refused a permit, or Spotify answered with a non-success status (a 401 that
    /// survives one token refresh included). Throws <see cref="ProviderRateLimitedException"/> when
    /// every attempt was rate limited, or the endpoint is blocked (see
    /// <see cref="MusicEnricherOptions.SpotifyApiMaxInlineRetryAfterSeconds"/>).
    /// </summary>
    public async Task<string?> GetJsonAsync(string clientId, string clientSecret, string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            return null;

        var endpoint = EndpointOf(url);
        ThrowIfEndpointBlocked(endpoint);

        // QueueLimit must be > 0 so concurrent enrichment workers wait for tokens instead of
        // failing immediately (matches AcoustIdService rate-limiter behavior).
        using var lease = await RateLimiter.AcquireAsync(options.Value.SpotifyApiRequestsPerSecond, ct);
        if (!lease.IsAcquired)
        {
            logger.LogWarning("Spotify catalog rate limiter could not grant a permit (disposed or canceled)");
            return null;
        }

        // Another request may have closed the endpoint while this one queued for a permit.
        ThrowIfEndpointBlocked(endpoint);

        var accessToken = await GetAccessTokenAsync(clientId, clientSecret, ct);
        if (accessToken is null)
            return null;

        var refreshedTokenAfter401 = false;
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

            var response = await httpClient.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = RetryAfterHeader.Read(response, time.GetUtcNow()) ?? RateLimitDefaultDelay;
                var waitInline = retryAfter <= TimeSpan.FromSeconds(options.Value.SpotifyApiMaxInlineRetryAfterSeconds);
                if (waitInline && attempt < MaxRetries)
                {
                    logger.LogWarning(
                        "Spotify catalog request rate limited. Retrying after {Delay}s (attempt {Attempt}/{Max})",
                        retryAfter.TotalSeconds, attempt + 1, MaxRetries);
                    await Task.Delay(retryAfter, time, ct);
                    continue;
                }

                if (!waitInline)
                {
                    BlockEndpoint(endpoint, time.GetUtcNow() + retryAfter);
                    logger.LogWarning(
                        "Spotify catalog endpoint {Endpoint} rate limited for {Delay}s; skipping it until then. {Body}",
                        endpoint, retryAfter.TotalSeconds, await response.Content.ReadAsStringAsync(ct));
                }

                throw new ProviderRateLimitedException(retryAfter);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized && !refreshedTokenAfter401)
            {
                InvalidateTokenCache(clientId);
                accessToken = await GetAccessTokenAsync(clientId, clientSecret, ct);
                refreshedTokenAfter401 = true;
                if (accessToken is null)
                    return null;
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                logger.LogWarning("Spotify catalog request unauthorized after token refresh: {Url}", url);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning("Spotify catalog request failed: {Status} {Url} {Body}", (int)response.StatusCode, url, body);
                return null;
            }

            return await response.Content.ReadAsStringAsync(ct);
        }

        return null;
    }

    private void ThrowIfEndpointBlocked(string endpoint)
    {
        var now = time.GetUtcNow();
        if (_endpointBlockedUntil.TryGetValue(endpoint, out var until) && until > now)
            throw new ProviderRateLimitedException(until - now);
    }

    // Extend-only, so a shorter wait reported later never reopens an endpoint early.
    private void BlockEndpoint(string endpoint, DateTimeOffset until) =>
        _endpointBlockedUntil.AddOrUpdate(endpoint, until, (_, current) => current > until ? current : until);

    /// <summary>The Web API resource a URL addresses — <c>search</c>, <c>albums</c>, <c>tracks</c>, …</summary>
    internal static string EndpointOf(string url) =>
        new Uri(url).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "";

    private async Task<string?> GetAccessTokenAsync(string clientId, string clientSecret, CancellationToken ct)
    {
        var cacheKey = TokenCacheKey(clientId);
        if (cache.TryGetValue(cacheKey, out string? cached) && !string.IsNullOrEmpty(cached))
            return cached;

        var gate = _tokenLocks.GetOrAdd(clientId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(cacheKey, out cached) && !string.IsNullOrEmpty(cached))
                return cached;

            using var request = new HttpRequestMessage(HttpMethod.Post, AccountsTokenUrl);
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials"
            });
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

            var response = await httpClient.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Spotify client-credentials token request failed: {Status} {Body}",
                    (int)response.StatusCode, json);
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var accessToken = root.GetProperty("access_token").GetString();
            var expiresIn = root.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;
            if (string.IsNullOrEmpty(accessToken))
                return null;

            // Drop the token 90 s before Spotify does so an in-flight request never straddles the
            // expiry, but never trust an expires_in so short that the floor would be a refetch storm.
            var ttl = TimeSpan.FromSeconds(Math.Max(120, expiresIn - 90));
            cache.Set(cacheKey, accessToken, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl });
            return accessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    private void InvalidateTokenCache(string clientId) => cache.Remove(TokenCacheKey(clientId));

    private static string TokenCacheKey(string clientId) => $"spotify_cc_token:{clientId}";
}
