using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Metadata;
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
                ? ReleaseDateParser.ParseYear(rdEl.GetString()) : null;
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

    // --- Discover (editorial browse) ---

    public async Task<IReadOnlyList<DeezerGenre>> GetGenresAsync(CancellationToken ct = default)
    {
        const string cacheKey = "deezer_genres";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<DeezerGenre>? cached) && cached is not null)
            return cached;

        var json = await GetAsync($"{BaseUrl}/genre", ct);
        var genres = json is null ? [] : ParseGenres(json);
        CacheBrowse(cacheKey, genres);
        return genres;
    }

    public async Task<IReadOnlyList<DeezerPlaylistSummary>> GetChartPlaylistsAsync(long? genreId, int limit, CancellationToken ct = default)
    {
        var genre = genreId ?? 0;
        limit = Math.Clamp(limit, 1, 100);
        var cacheKey = $"deezer_chart_playlists:{genre}:{limit}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<DeezerPlaylistSummary>? cached) && cached is not null)
            return cached;

        var json = await GetAsync($"{BaseUrl}/chart/{genre}/playlists?limit={limit}", ct);
        var playlists = json is null ? [] : ParsePlaylistList(json);
        CacheBrowse(cacheKey, playlists);
        return playlists;
    }

    public async Task<IReadOnlyList<DeezerPlaylistSummary>> SearchPlaylistsAsync(string query, int limit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        limit = Math.Clamp(limit, 1, 100);
        var raw = $"{limit}|{query}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        var cacheKey = $"deezer_search_playlists:{hash}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<DeezerPlaylistSummary>? cached) && cached is not null)
            return cached;

        var json = await GetAsync($"{BaseUrl}/search/playlist?q={Uri.EscapeDataString(query)}&limit={limit}", ct);
        var playlists = json is null ? [] : ParsePlaylistList(json);
        CacheBrowse(cacheKey, playlists);
        return playlists;
    }

    public async Task<DeezerPlaylistSummary?> GetPlaylistAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        var cacheKey = $"deezer_playlist:{id}";
        if (cache.TryGetValue(cacheKey, out DeezerPlaylistSummary? cached))
            return cached;

        var json = await GetAsync($"{BaseUrl}/playlist/{Uri.EscapeDataString(id)}", ct);
        var playlist = json is null ? null : ParsePlaylistSummary(json);
        cache.Set(cacheKey, playlist, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(Math.Max(1, options.Value.DeezerBrowseCacheMinutes)),
        });
        return playlist;
    }

    public async Task<DeezerPlaylistTracksResult> GetPlaylistTracksAsync(string id, int? maxTracks = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return new DeezerPlaylistTracksResult([], IsComplete: true);

        const int pageSize = 100;
        var all = new List<DeezerPlaylistTrack>();
        var index = 0;
        while (true)
        {
            var json = await GetAsync($"{BaseUrl}/playlist/{Uri.EscapeDataString(id)}/tracks?index={index}&limit={pageSize}", ct);
            if (json is null)
                // A page fetch failed mid-run (non-429 error). Return what we have flagged incomplete so
                // callers don't persist a checksum that would permanently hide the never-fetched tail.
                return new DeezerPlaylistTracksResult(all, IsComplete: false);

            var (page, hasNext) = ParsePlaylistTracksPage(json);
            all.AddRange(page);

            // Honor a caller-supplied cap (the detail browse view only previews a bounded slice) so a
            // huge editorial playlist can't page thousands of tracks in the request path. A capped fetch
            // is not "complete".
            if (maxTracks is { } cap && all.Count >= cap)
            {
                if (all.Count > cap) all.RemoveRange(cap, all.Count - cap);
                return new DeezerPlaylistTracksResult(all, IsComplete: false);
            }

            // Follow paging via the presence of a `next` link; fall back to page-size heuristics so a
            // provider that omits `next` still terminates. Guard against runaway loops.
            if (!hasNext || page.Count == 0 || page.Count < pageSize) break;
            index += pageSize;
            if (index > 10_000) break;
        }

        return new DeezerPlaylistTracksResult(all, IsComplete: true);
    }

    private void CacheBrowse<T>(string cacheKey, T value) =>
        cache.Set(cacheKey, value, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(Math.Max(1, options.Value.DeezerBrowseCacheMinutes)),
        });

    private static IReadOnlyList<DeezerGenre> ParseGenres(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return [];

            var list = new List<DeezerGenre>();
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64() : -1;
                if (id < 0) continue;
                var name = item.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String ? nEl.GetString() ?? "" : "";
                var picture = FirstString(item, "picture_medium", "picture_big", "picture");
                list.Add(new DeezerGenre(id, name, picture));
            }
            return list;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<DeezerPlaylistSummary> ParsePlaylistList(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return [];

            var list = new List<DeezerPlaylistSummary>();
            foreach (var item in data.EnumerateArray())
            {
                var parsed = ParsePlaylistElement(item);
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

    private static DeezerPlaylistSummary? ParsePlaylistSummary(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ParsePlaylistElement(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DeezerPlaylistSummary? ParsePlaylistElement(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        var id = el.TryGetProperty("id", out var idEl)
            ? idEl.ValueKind switch
            {
                JsonValueKind.Number => idEl.GetInt64().ToString(),
                JsonValueKind.String => idEl.GetString() ?? "",
                _ => "",
            }
            : "";
        if (string.IsNullOrEmpty(id)) return null;

        var title = el.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String ? tEl.GetString() ?? "" : "";
        string? description = el.TryGetProperty("description", out var dEl) && dEl.ValueKind == JsonValueKind.String
            ? NullIfEmpty(dEl.GetString())
            : null;
        var cover = FirstString(el, "picture_xl", "picture_big", "picture_medium", "picture");
        var trackCount = el.TryGetProperty("nb_tracks", out var nbEl) && nbEl.ValueKind == JsonValueKind.Number ? nbEl.GetInt32() : 0;
        string? checksum = el.TryGetProperty("checksum", out var cEl) && cEl.ValueKind == JsonValueKind.String ? cEl.GetString() : null;

        string? creator = null;
        if (el.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object &&
            user.TryGetProperty("name", out var un) && un.ValueKind == JsonValueKind.String)
            creator = un.GetString();
        else if (el.TryGetProperty("creator", out var cr) && cr.ValueKind == JsonValueKind.Object &&
            cr.TryGetProperty("name", out var crn) && crn.ValueKind == JsonValueKind.String)
            creator = crn.GetString();

        return new DeezerPlaylistSummary(id, title, description, cover, trackCount, creator, checksum);
    }

    private static (IReadOnlyList<DeezerPlaylistTrack> Tracks, bool HasNext) ParsePlaylistTracksPage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return ([], false);

            var list = new List<DeezerPlaylistTrack>();
            foreach (var t in data.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.Object) continue;
                var id = t.TryGetProperty("id", out var idEl)
                    ? idEl.ValueKind switch
                    {
                        JsonValueKind.Number => idEl.GetInt64().ToString(),
                        JsonValueKind.String => idEl.GetString() ?? "",
                        _ => "",
                    }
                    : "";
                if (string.IsNullOrEmpty(id)) continue;

                var title = t.TryGetProperty("title", out var tiEl) && tiEl.ValueKind == JsonValueKind.String ? tiEl.GetString() ?? "" : "";
                var durSec = t.TryGetProperty("duration", out var duEl) && duEl.ValueKind == JsonValueKind.Number ? duEl.GetInt32() : 0;

                var artist = "";
                if (t.TryGetProperty("artist", out var aEl) && aEl.ValueKind == JsonValueKind.Object &&
                    aEl.TryGetProperty("name", out var an) && an.ValueKind == JsonValueKind.String)
                    artist = an.GetString() ?? "";

                string? album = null;
                string? cover = null;
                if (t.TryGetProperty("album", out var albEl) && albEl.ValueKind == JsonValueKind.Object)
                {
                    if (albEl.TryGetProperty("title", out var alt) && alt.ValueKind == JsonValueKind.String)
                        album = NullIfEmpty(alt.GetString());
                    cover = FirstString(albEl, "cover_medium", "cover_big", "cover_xl", "cover");
                }

                list.Add(new DeezerPlaylistTrack(id, title, artist, album, durSec * 1000, cover));
            }

            var hasNext = root.TryGetProperty("next", out var nextEl) && nextEl.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(nextEl.GetString());
            return (list, hasNext);
        }
        catch (JsonException)
        {
            return ([], false);
        }
    }

    private static string? FirstString(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }
        return null;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

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

        // ISRC, release date, track position and contributors are only present on full track detail.
        string? isrc = null;
        int? releaseYear = null;
        int? trackNumber = null;
        string? artists = null;
        if (fullDetail)
        {
            if (track.TryGetProperty("isrc", out var isrcProp) && isrcProp.ValueKind == JsonValueKind.String)
                isrc = isrcProp.GetString();

            if (track.TryGetProperty("release_date", out var rd) && rd.ValueKind == JsonValueKind.String)
                releaseYear = ReleaseDateParser.ParseYear(rd.GetString());

            if (track.TryGetProperty("track_position", out var tp) && tp.ValueKind == JsonValueKind.Number)
                trackNumber = tp.GetInt32();

            artists = ParseContributors(track);
        }

        return new DeezerCatalogTrack(id, title, artist, albumName, releaseYear, trackNumber, durationSec * 1000, isrc,
            Artists: artists);
    }

    /// <summary>
    /// Discrete credited artists from the full track detail's <c>contributors</c> array (the search
    /// payload only carries the single primary <c>artist</c>). Featured guests are listed with
    /// <c>role: "Featured"</c> and still belong in the per-artist credit, so every named contributor
    /// is kept (deduped, in payload order).
    /// </summary>
    private static string? ParseContributors(JsonElement track)
    {
        if (!track.TryGetProperty("contributors", out var contributors) || contributors.ValueKind != JsonValueKind.Array)
            return null;

        var names = new List<string>();
        foreach (var contributor in contributors.EnumerateArray())
        {
            if (contributor.ValueKind == JsonValueKind.Object
                && contributor.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(name.GetString())
                && !names.Contains(name.GetString()!.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                names.Add(name.GetString()!.Trim());
            }
        }

        return MusicHoarder.Api.Metadata.MultiValue.Join(names);
    }
}
