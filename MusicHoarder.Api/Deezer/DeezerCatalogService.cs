using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Deezer;

/// <summary>
/// Free, no-auth Deezer catalog access (<c>api.deezer.com</c>). Mirrors the Spotify catalog
/// service shape (in-memory cache + shared token-bucket rate limiter), minus the OAuth dance.
/// </summary>
public sealed class DeezerCatalogService(
    HttpClient httpClient,
    IMemoryCache cache,
    IOptions<MusicEnricherOptions> options,
    ILogger<DeezerCatalogService> logger) : IDeezerCatalogService
{
    private const string BaseUrl = "https://api.deezer.com";
    private static readonly TimeSpan RateLimitDefaultDelay = TimeSpan.FromSeconds(5);
    private const int MaxRetries = 3;

    // Deezer signals quota exhaustion with HTTP 200 + an error body (code 4), not just 429.
    private const int DeezerQuotaErrorCode = 4;

    private static readonly object RateLimiterLock = new();
    private static TokenBucketRateLimiter? _sharedRateLimiter;
    private static int _sharedRate = -1;

    public async Task<DeezerCatalogTrack?> LookupByIsrcAsync(string isrc, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(isrc))
            return null;

        var cacheKey = $"deezer_isrc:{isrc}";
        if (cache.TryGetValue(cacheKey, out DeezerCatalogTrack? cached))
            return cached;

        var json = await GetAsync($"{BaseUrl}/track/isrc:{Uri.EscapeDataString(isrc)}", ct);
        var track = json is null ? null : ParseTrackDetail(json);
        CacheTrack(cacheKey, track);
        return track;
    }

    public async Task<IReadOnlyList<DeezerCatalogTrack>> SearchTracksAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var opts = options.Value;
        var limit = Math.Clamp(opts.DeezerApiSearchLimit, 1, 50);
        var cacheKey = BuildSearchCacheKey(query, limit);
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<DeezerCatalogTrack>? cached) && cached is not null)
            return cached;

        var url = $"{BaseUrl}/search?q={Uri.EscapeDataString(query)}&limit={limit}";
        var json = await GetAsync(url, ct);
        var tracks = json is null ? [] : ParseSearchResponse(json);
        cache.Set(cacheKey, tracks, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(Math.Max(1, opts.DeezerApiSearchCacheMinutes)),
        });
        return tracks;
    }

    /// <summary>Hydrate a search hit to full detail (ISRC / release year / track position).</summary>
    public async Task<DeezerCatalogTrack?> LookupByIdAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var cacheKey = $"deezer_track:{id}";
        if (cache.TryGetValue(cacheKey, out DeezerCatalogTrack? cached))
            return cached;

        var json = await GetAsync($"{BaseUrl}/track/{Uri.EscapeDataString(id)}", ct);
        var track = json is null ? null : ParseTrackDetail(json);
        CacheTrack(cacheKey, track);
        return track;
    }

    private void CacheTrack(string cacheKey, DeezerCatalogTrack? track) =>
        cache.Set(cacheKey, track, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(Math.Max(1, options.Value.DeezerApiSearchCacheMinutes)),
        });

    /// <summary>GET with rate limiting + retry; returns the raw JSON body or null on failure.
    /// Throws <see cref="ProviderRateLimitedException"/> when Deezer signals quota exhaustion.</summary>
    private async Task<string?> GetAsync(string url, CancellationToken ct)
    {
        var limiter = GetRateLimiter(options.Value.DeezerApiRequestsPerSecond);
        using var lease = await limiter.AcquireAsync(permitCount: 1, ct);
        if (!lease.IsAcquired)
        {
            logger.LogWarning("Deezer rate limiter could not grant a permit (disposed or canceled)");
            return null;
        }

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "MusicHoarder/1.0 (https://github.com/Jeffreyyvdb/MusicHoarder)");

            var response = await httpClient.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? RateLimitDefaultDelay;
                logger.LogWarning("Deezer rate limited (HTTP 429). Retry after {Delay}s (attempt {Attempt}/{Max})",
                    retryAfter.TotalSeconds, attempt + 1, MaxRetries);
                if (attempt < MaxRetries)
                {
                    await Task.Delay(retryAfter, ct);
                    continue;
                }

                throw new ProviderRateLimitedException(retryAfter);
            }

            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning("Deezer request failed: {Status} {Body}", (int)response.StatusCode, errBody);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            if (IsQuotaError(json))
            {
                logger.LogWarning("Deezer quota exceeded (error body). Retry after {Delay}s (attempt {Attempt}/{Max})",
                    RateLimitDefaultDelay.TotalSeconds, attempt + 1, MaxRetries);
                if (attempt < MaxRetries)
                {
                    await Task.Delay(RateLimitDefaultDelay, ct);
                    continue;
                }

                throw new ProviderRateLimitedException(RateLimitDefaultDelay);
            }

            return json;
        }

        return null;
    }

    private static bool IsQuotaError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out var err) &&
                err.ValueKind == JsonValueKind.Object &&
                err.TryGetProperty("code", out var code) &&
                code.ValueKind == JsonValueKind.Number)
            {
                return code.GetInt32() == DeezerQuotaErrorCode;
            }
        }
        catch (JsonException)
        {
            // Treat unparseable body as non-quota; the caller's parse will yield no tracks.
        }

        return false;
    }

    private static string BuildSearchCacheKey(string query, int limit)
    {
        var raw = $"{limit}|{query}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        return $"deezer_search:{hash}";
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

    private static IReadOnlyList<DeezerCatalogTrack> ParseSearchResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return [];

            var list = new List<DeezerCatalogTrack>();
            foreach (var item in data.EnumerateArray())
            {
                var parsed = ParseTrack(item, fullDetail: false);
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

    private static DeezerCatalogTrack? ParseTrackDetail(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ParseTrack(doc.RootElement, fullDetail: true);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DeezerCatalogTrack? ParseTrack(JsonElement track, bool fullDetail)
    {
        if (track.ValueKind != JsonValueKind.Object)
            return null;

        var id = track.TryGetProperty("id", out var idProp)
            ? idProp.ValueKind switch
            {
                JsonValueKind.Number => idProp.GetInt64().ToString(),
                JsonValueKind.String => idProp.GetString() ?? "",
                _ => "",
            }
            : "";
        if (string.IsNullOrEmpty(id))
            return null;

        var title = track.TryGetProperty("title", out var tProp) && tProp.ValueKind == JsonValueKind.String
            ? tProp.GetString() ?? ""
            : "";

        var durationSec = track.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number
            ? dur.GetInt32()
            : 0;

        var artist = "";
        if (track.TryGetProperty("artist", out var artistEl) && artistEl.ValueKind == JsonValueKind.Object &&
            artistEl.TryGetProperty("name", out var artistName) && artistName.ValueKind == JsonValueKind.String)
            artist = artistName.GetString() ?? "";

        var albumName = "";
        if (track.TryGetProperty("album", out var albumEl) && albumEl.ValueKind == JsonValueKind.Object &&
            albumEl.TryGetProperty("title", out var albumTitle) && albumTitle.ValueKind == JsonValueKind.String)
            albumName = albumTitle.GetString() ?? "";

        // ISRC, release date and track position are only present on full track detail.
        string? isrc = null;
        int? releaseYear = null;
        int? trackNumber = null;
        if (fullDetail)
        {
            if (track.TryGetProperty("isrc", out var isrcProp) && isrcProp.ValueKind == JsonValueKind.String)
                isrc = isrcProp.GetString();

            if (track.TryGetProperty("release_date", out var rd) && rd.ValueKind == JsonValueKind.String)
                releaseYear = ParseReleaseYear(rd.GetString());

            if (track.TryGetProperty("track_position", out var tp) && tp.ValueKind == JsonValueKind.Number)
                trackNumber = tp.GetInt32();
        }

        return new DeezerCatalogTrack(id, title, artist, albumName, releaseYear, trackNumber, durationSec * 1000, isrc);
    }

    private static int? ParseReleaseYear(string? releaseDate)
    {
        if (string.IsNullOrWhiteSpace(releaseDate))
            return null;
        var part = releaseDate.Length >= 4 ? releaseDate[..4] : releaseDate;
        return int.TryParse(part, out var y) && y is > 1000 and < 3000 ? y : null;
    }
}
