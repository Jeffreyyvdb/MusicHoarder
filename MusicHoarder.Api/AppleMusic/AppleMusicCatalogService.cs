using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.AppleMusic;

/// <summary>
/// Free, no-auth Apple/iTunes catalog access (<c>itunes.apple.com/search</c>). Mirrors the
/// Spotify catalog service shape (in-memory cache + shared token-bucket rate limiter), minus the
/// OAuth dance. iTunes is aggressively throttled (~20 req/min), so keep RPS low.
/// </summary>
public sealed class AppleMusicCatalogService(
    HttpClient httpClient,
    IMemoryCache cache,
    IOptions<MusicEnricherOptions> options,
    ILogger<AppleMusicCatalogService> logger) : IAppleMusicCatalogService
{
    private const string SearchUrl = "https://itunes.apple.com/search";
    private static readonly TimeSpan RateLimitDefaultDelay = TimeSpan.FromSeconds(5);
    // One in-process retry covers a transient blip; an IP-wide throttle storm is handled by the
    // shared backoff window + the orchestrator's deferred RetryAfterUtc re-sweep, not by grinding here.
    private const int MaxRetries = 2;

    private static readonly object RateLimiterLock = new();
    private static TokenBucketRateLimiter? _sharedRateLimiter;
    private static int _sharedRate = -1;

    // Shared backoff window across all instances: iTunes throttles by IP, so once one call is
    // rate-limited every concurrent/subsequent call would be too. Short-circuit them instead of
    // each grinding through in-process retries — the orchestrator defers them via RetryAfterUtc.
    private static long _backoffUntilUtcTicks;

    public async Task<IReadOnlyList<AppleMusicCatalogTrack>> SearchTracksAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var opts = options.Value;
        var limit = Math.Clamp(opts.AppleMusicApiSearchLimit, 1, 50);
        var country = string.IsNullOrWhiteSpace(opts.AppleMusicCountry) ? "US" : opts.AppleMusicCountry.Trim();

        var cacheKey = BuildSearchCacheKey(query, limit, country);
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<AppleMusicCatalogTrack>? cached) && cached is not null)
            return cached;

        // Inside an active throttle window: bail out without an HTTP call or any delay.
        var backoffUntil = BackoffUntil;
        var now = DateTime.UtcNow;
        if (backoffUntil > now)
            throw new ProviderRateLimitedException(backoffUntil - now);

        var limiter = GetRateLimiter(opts.AppleMusicApiRequestsPerSecond);
        using var lease = await limiter.AcquireAsync(permitCount: 1, ct);
        if (!lease.IsAcquired)
        {
            logger.LogWarning("Apple Music rate limiter could not grant a permit (disposed or canceled)");
            return [];
        }

        var url = $"{SearchUrl}?term={Uri.EscapeDataString(query)}&entity=song&limit={limit}&country={Uri.EscapeDataString(country)}";

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "MusicHoarder/1.0 (https://github.com/Jeffreyyvdb/MusicHoarder)");

            var response = await httpClient.SendAsync(request, ct);

            // iTunes throttles with 403 as well as the standard 429; treat both as rate-limited.
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? RateLimitDefaultDelay;
                // Open the shared backoff window so concurrent/subsequent callers short-circuit.
                ExtendBackoff(retryAfter);
                logger.LogWarning("Apple Music rate limited ({Status}). Retry after {Delay}s (attempt {Attempt}/{Max})",
                    (int)response.StatusCode, retryAfter.TotalSeconds, attempt, MaxRetries);
                if (attempt < MaxRetries)
                {
                    await Task.Delay(retryAfter, ct);
                    continue;
                }

                throw new ProviderRateLimitedException(retryAfter);
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning("Apple Music search failed: {Status} {Body}", (int)response.StatusCode, body);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var tracks = ParseSearchResponse(json);
            cache.Set(cacheKey, tracks, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(Math.Max(1, opts.AppleMusicApiSearchCacheMinutes)),
            });
            return tracks;
        }

        return [];
    }

    private static DateTime BackoffUntil => new(Interlocked.Read(ref _backoffUntilUtcTicks), DateTimeKind.Utc);

    internal static void ResetBackoffForTests() => Interlocked.Exchange(ref _backoffUntilUtcTicks, 0);

    private static void ExtendBackoff(TimeSpan retryAfter)
    {
        var candidate = (DateTime.UtcNow + retryAfter).Ticks;
        long current;
        do
        {
            current = Interlocked.Read(ref _backoffUntilUtcTicks);
            if (candidate <= current)
                return;
        } while (Interlocked.CompareExchange(ref _backoffUntilUtcTicks, candidate, current) != current);
    }

    private static string BuildSearchCacheKey(string query, int limit, string country)
    {
        var raw = $"{limit}|{country}|{query}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        return $"applemusic_search:{hash}";
    }

    private static TokenBucketRateLimiter GetRateLimiter(int requestsPerSecond)
    {
        lock (RateLimiterLock)
        {
            if (_sharedRateLimiter is not null && _sharedRate == requestsPerSecond)
                return _sharedRateLimiter;

            _sharedRateLimiter?.Dispose();
            _sharedRate = requestsPerSecond;
            _sharedRateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = requestsPerSecond,
                TokensPerPeriod = requestsPerSecond,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = int.MaxValue,
                AutoReplenishment = true,
            });
            return _sharedRateLimiter;
        }
    }

    private static IReadOnlyList<AppleMusicCatalogTrack> ParseSearchResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                return [];

            var list = new List<AppleMusicCatalogTrack>();
            foreach (var item in results.EnumerateArray())
            {
                var parsed = ParseTrack(item);
                if (parsed is not null)
                    list.Add(parsed);
            }

            return list;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static AppleMusicCatalogTrack? ParseTrack(JsonElement track)
    {
        if (track.ValueKind != JsonValueKind.Object)
            return null;

        var id = track.TryGetProperty("trackId", out var idProp) && idProp.ValueKind == JsonValueKind.Number
            ? idProp.GetInt64().ToString()
            : "";
        if (string.IsNullOrEmpty(id))
            return null;

        var title = track.TryGetProperty("trackName", out var tProp) && tProp.ValueKind == JsonValueKind.String
            ? tProp.GetString() ?? ""
            : "";

        var artist = track.TryGetProperty("artistName", out var aProp) && aProp.ValueKind == JsonValueKind.String
            ? aProp.GetString() ?? ""
            : "";

        var albumName = track.TryGetProperty("collectionName", out var cProp) && cProp.ValueKind == JsonValueKind.String
            ? cProp.GetString() ?? ""
            : "";

        var durationMs = track.TryGetProperty("trackTimeMillis", out var dur) && dur.ValueKind == JsonValueKind.Number
            ? dur.GetInt32()
            : 0;

        int? trackNumber = null;
        if (track.TryGetProperty("trackNumber", out var tn) && tn.ValueKind == JsonValueKind.Number)
            trackNumber = tn.GetInt32();

        int? releaseYear = null;
        if (track.TryGetProperty("releaseDate", out var rd) && rd.ValueKind == JsonValueKind.String)
            releaseYear = ParseReleaseYear(rd.GetString());

        return new AppleMusicCatalogTrack(id, title, artist, albumName, releaseYear, trackNumber, durationMs, null);
    }

    private static int? ParseReleaseYear(string? releaseDate)
    {
        if (string.IsNullOrWhiteSpace(releaseDate))
            return null;
        var part = releaseDate.Length >= 4 ? releaseDate[..4] : releaseDate;
        return int.TryParse(part, out var y) && y is > 1000 and < 3000 ? y : null;
    }
}
