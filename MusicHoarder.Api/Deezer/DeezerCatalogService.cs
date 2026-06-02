using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.RateLimiting;

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

    private static readonly ReconfigurableRateLimiter RateLimiter = new();

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

    public async Task<string?> SearchAlbumIdAsync(string artist, string album, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(album)) return null;
        var query = $"{artist} {album}".Trim();
        var json = await GetAsync($"{BaseUrl}/search/album?q={Uri.EscapeDataString(query)}&limit=5", ct);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.Number or JsonValueKind.String)
                        return id.ValueKind == JsonValueKind.Number ? id.GetInt64().ToString() : id.GetString();
                }
            }
        }
        catch (JsonException) { /* fall through */ }
        return null;
    }

    public async Task<DeezerAlbumDetail?> GetAlbumAsync(string albumId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(albumId)) return null;
        var json = await GetAsync($"{BaseUrl}/album/{Uri.EscapeDataString(albumId)}", ct);
        return json is null ? null : ParseAlbum(json);
    }

    private static DeezerAlbumDetail? ParseAlbum(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var id = root.TryGetProperty("id", out var idEl)
                ? idEl.ValueKind switch
                {
                    JsonValueKind.Number => idEl.GetInt64().ToString(),
                    JsonValueKind.String => idEl.GetString() ?? "",
                    _ => "",
                }
                : "";
            if (string.IsNullOrEmpty(id)) return null;

            var title = root.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String ? tEl.GetString() : null;
            int? year = root.TryGetProperty("release_date", out var rdEl) && rdEl.ValueKind == JsonValueKind.String
                ? ParseReleaseYear(rdEl.GetString()) : null;
            string? artist = root.TryGetProperty("artist", out var aEl) && aEl.ValueKind == JsonValueKind.Object &&
                aEl.TryGetProperty("name", out var an) && an.ValueKind == JsonValueKind.String ? an.GetString() : null;
            string? cover = root.TryGetProperty("cover_xl", out var cEl) && cEl.ValueKind == JsonValueKind.String
                ? cEl.GetString()
                : root.TryGetProperty("cover_big", out var cbEl) && cbEl.ValueKind == JsonValueKind.String ? cbEl.GetString() : null;

            var tracks = new List<DeezerAlbumTrackItem>();
            if (root.TryGetProperty("tracks", out var tracksEl) && tracksEl.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Array)
            {
                var ordinal = 0;
                foreach (var t in data.EnumerateArray())
                {
                    if (t.ValueKind != JsonValueKind.Object) continue;
                    ordinal++;
                    var disc = t.TryGetProperty("disk_number", out var dk) && dk.ValueKind == JsonValueKind.Number ? dk.GetInt32() : 1;
                    var pos = t.TryGetProperty("track_position", out var tp) && tp.ValueKind == JsonValueKind.Number ? tp.GetInt32() : ordinal;
                    var tTitle = t.TryGetProperty("title", out var ti) && ti.ValueKind == JsonValueKind.String ? ti.GetString() : null;
                    var durSec = t.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number ? du.GetInt32() : 0;
                    var tId = t.TryGetProperty("id", out var tid)
                        ? tid.ValueKind switch
                        {
                            JsonValueKind.Number => tid.GetInt64().ToString(),
                            JsonValueKind.String => tid.GetString(),
                            _ => null,
                        }
                        : null;
                    tracks.Add(new DeezerAlbumTrackItem(disc, pos, tTitle, durSec * 1000, tId));
                }
            }

            return new DeezerAlbumDetail(id, title, artist, year, cover, tracks);
        }
        catch (JsonException)
        {
            return null;
        }
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
        using var lease = await RateLimiter.AcquireAsync(options.Value.DeezerApiRequestsPerSecond, ct);
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
