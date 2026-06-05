using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.RateLimiting;

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
    private const string LookupUrl = "https://itunes.apple.com/lookup";
    // No Retry-After header means we've likely blown the per-minute budget; back off a full minute
    // so the quota resets before we resume (server-provided Retry-After is honored when present).
    private static readonly TimeSpan RateLimitDefaultDelay = TimeSpan.FromSeconds(60);
    // One in-process retry covers a transient blip; an IP-wide throttle storm is handled by the
    // shared backoff window + the orchestrator's deferred RetryAfterUtc re-sweep, not by grinding here.
    private const int MaxRetries = 2;

    private static readonly ReconfigurableRateLimiter RateLimiter = new();

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

        using var lease = await RateLimiter.AcquireAsync(1, RequestInterval(opts), ct);
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

    public async Task<string?> SearchAlbumIdAsync(string artist, string album, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(album)) return null;
        var opts = options.Value;
        var country = string.IsNullOrWhiteSpace(opts.AppleMusicCountry) ? "US" : opts.AppleMusicCountry.Trim();
        var term = Uri.EscapeDataString($"{artist} {album}".Trim());
        var url = $"{SearchUrl}?term={term}&entity=album&limit=5&country={Uri.EscapeDataString(country)}";
        var json = await FetchJsonAsync(url, ct);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in results.EnumerateArray())
                {
                    if (item.TryGetProperty("collectionId", out var cid) && cid.ValueKind == JsonValueKind.Number)
                        return cid.GetInt64().ToString();
                }
            }
        }
        catch (JsonException) { /* fall through */ }
        return null;
    }

    public async Task<AppleAlbumDetail?> GetAlbumAsync(string collectionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(collectionId)) return null;
        var opts = options.Value;
        var country = string.IsNullOrWhiteSpace(opts.AppleMusicCountry) ? "US" : opts.AppleMusicCountry.Trim();
        var url = $"{LookupUrl}?id={Uri.EscapeDataString(collectionId)}&entity=song&country={Uri.EscapeDataString(country)}";
        var json = await FetchJsonAsync(url, ct);
        return json is null ? null : ParseAlbum(json);
    }

    private static AppleAlbumDetail? ParseAlbum(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                return null;

            string? id = null, name = null, artist = null, artwork = null;
            int? year = null;
            var tracks = new List<AppleAlbumTrackItem>();

            foreach (var item in results.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var wrapper = item.TryGetProperty("wrapperType", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null;

                if (wrapper == "collection")
                {
                    if (item.TryGetProperty("collectionId", out var cid) && cid.ValueKind == JsonValueKind.Number) id = cid.GetInt64().ToString();
                    if (item.TryGetProperty("collectionName", out var cn) && cn.ValueKind == JsonValueKind.String) name = cn.GetString();
                    if (item.TryGetProperty("artistName", out var an) && an.ValueKind == JsonValueKind.String) artist = an.GetString();
                    if (item.TryGetProperty("artworkUrl100", out var aw) && aw.ValueKind == JsonValueKind.String) artwork = aw.GetString();
                    if (item.TryGetProperty("releaseDate", out var rd) && rd.ValueKind == JsonValueKind.String) year = ParseReleaseYear(rd.GetString());
                }
                else if (wrapper == "track")
                {
                    var disc = item.TryGetProperty("discNumber", out var dn) && dn.ValueKind == JsonValueKind.Number ? dn.GetInt32() : 1;
                    var num = item.TryGetProperty("trackNumber", out var tn) && tn.ValueKind == JsonValueKind.Number ? tn.GetInt32() : 0;
                    var tName = item.TryGetProperty("trackName", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() : null;
                    var dur = item.TryGetProperty("trackTimeMillis", out var tm) && tm.ValueKind == JsonValueKind.Number ? tm.GetInt32() : 0;
                    var tId = item.TryGetProperty("trackId", out var ti) && ti.ValueKind == JsonValueKind.Number ? ti.GetInt64().ToString() : null;
                    tracks.Add(new AppleAlbumTrackItem(disc, num, tName, dur, tId));
                }
            }

            return id is null ? null : new AppleAlbumDetail(id, name, artist, year, artwork, tracks);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Rate-limited iTunes GET sharing the backoff window; returns the JSON body or null.</summary>
    private async Task<string?> FetchJsonAsync(string url, CancellationToken ct)
    {
        var backoffUntil = BackoffUntil;
        var now = DateTime.UtcNow;
        if (backoffUntil > now)
            throw new ProviderRateLimitedException(backoffUntil - now);

        using var lease = await RateLimiter.AcquireAsync(1, RequestInterval(options.Value), ct);
        if (!lease.IsAcquired)
            return null;

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "MusicHoarder/1.0 (https://github.com/Jeffreyyvdb/MusicHoarder)");

            var response = await httpClient.SendAsync(request, ct);

            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? RateLimitDefaultDelay;
                ExtendBackoff(retryAfter);
                if (attempt < MaxRetries) { await Task.Delay(retryAfter, ct); continue; }
                throw new ProviderRateLimitedException(retryAfter);
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Apple Music GET failed: {Status} {Url}", (int)response.StatusCode, url);
                return null;
            }

            return await response.Content.ReadAsStringAsync(ct);
        }

        return null;
    }

    // Strictly smooth spacing (1 permit per interval) rather than a per-minute burst — a burst of
    // N then idle would still trip iTunes' per-minute throttle.
    private static TimeSpan RequestInterval(MusicEnricherOptions opts)
        => TimeSpan.FromSeconds(60.0 / Math.Clamp(opts.AppleMusicApiRequestsPerMinute, 1, 30));

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
