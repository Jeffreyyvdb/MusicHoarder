using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Spotify;

/// <summary>
/// The Spotify Web API as a catalog: builds each lookup's URL, hands it to
/// <see cref="SpotifyClientCredentialsClient"/> (which owns the app token, the rate limit and the
/// retry policy), and hands the JSON that comes back to <see cref="SpotifyCatalogJson"/>, which maps
/// it into the catalog records the enrichment, artwork and tracklist consumers read. Track searches
/// are additionally cached by query.
/// </summary>
public sealed class SpotifyCatalogSearchService(
    SpotifyClientCredentialsClient api,
    IMemoryCache cache,
    IOptions<MusicEnricherOptions> options) : ISpotifyCatalogSearchService
{
    private const string ApiSearchUrl = "https://api.spotify.com/v1/search";

    public async Task<IReadOnlyList<SpotifyCatalogTrack>> SearchTracksAsync(
        string clientId,
        string clientSecret,
        string query,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            return [];

        var opts = options.Value;
        var limit = Math.Clamp(opts.SpotifyApiSearchLimit, 1, 50);
        var market = string.IsNullOrWhiteSpace(opts.SpotifyApiMarket) ? null : opts.SpotifyApiMarket.Trim();

        var cacheKey = BuildSearchCacheKey(query, limit, market);
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<SpotifyCatalogTrack>? cached) && cached is not null)
            return cached;

        var q = Uri.EscapeDataString(query);
        var url = $"{ApiSearchUrl}?q={q}&type=track&limit={limit}";
        if (market is not null)
            url += $"&market={Uri.EscapeDataString(market)}";

        var json = await api.GetJsonAsync(clientId, clientSecret, url, ct);
        if (json is null)
            return [];

        var tracks = SpotifyCatalogJson.ParseSearchResponse(json);
        cache.Set(
            cacheKey,
            tracks,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(Math.Max(1, opts.SpotifyApiSearchCacheMinutes))
            });
        return tracks;
    }

    public async Task<SpotifyCatalogTrack?> GetTrackAsync(string clientId, string clientSecret, string trackId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(trackId)) return null;
        var json = await api.GetJsonAsync(clientId, clientSecret, $"https://api.spotify.com/v1/tracks/{Uri.EscapeDataString(trackId)}", ct);
        return json is null ? null : SpotifyCatalogJson.ParseTrackDetail(json);
    }

    public async Task<string?> GetTrackAlbumIdAsync(string clientId, string clientSecret, string trackId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(trackId)) return null;
        var json = await api.GetJsonAsync(clientId, clientSecret, $"https://api.spotify.com/v1/tracks/{Uri.EscapeDataString(trackId)}", ct);
        return json is null ? null : SpotifyCatalogJson.ParseTrackAlbumId(json);
    }

    public async Task<string?> SearchAlbumIdAsync(string clientId, string clientSecret, string artist, string album, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(album)) return null;
        var q = Uri.EscapeDataString($"album:{album} artist:{artist}".Trim());
        var json = await api.GetJsonAsync(clientId, clientSecret, $"{ApiSearchUrl}?q={q}&type=album&limit=5", ct);
        return json is null ? null : SpotifyCatalogJson.ParseFirstAlbumId(json);
    }

    public async Task<IReadOnlyList<SpotifyAlbumCandidate>> SearchAlbumCandidatesAsync(string clientId, string clientSecret, string artist, string album, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(album)) return [];
        var q = Uri.EscapeDataString($"album:{album} artist:{artist}".Trim());
        var json = await api.GetJsonAsync(clientId, clientSecret, $"{ApiSearchUrl}?q={q}&type=album&limit=5", ct);
        return json is null ? [] : SpotifyCatalogJson.ParseAlbumCandidates(json);
    }

    public async Task<IReadOnlyList<SpotifyArtistCandidate>> SearchArtistCandidatesAsync(string clientId, string clientSecret, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return [];
        var q = Uri.EscapeDataString($"artist:{name}".Trim());
        var json = await api.GetJsonAsync(clientId, clientSecret, $"{ApiSearchUrl}?q={q}&type=artist&limit=5", ct);
        return json is null ? [] : SpotifyCatalogJson.ParseArtistCandidates(json);
    }

    public async Task<SpotifyAlbumDetail?> GetAlbumAsync(string clientId, string clientSecret, string albumId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(albumId)) return null;
        var json = await api.GetJsonAsync(clientId, clientSecret, $"https://api.spotify.com/v1/albums/{Uri.EscapeDataString(albumId)}", ct);
        return json is null ? null : SpotifyCatalogJson.ParseAlbum(json);
    }

    public async Task<string?> SearchTrackIdByIsrcAsync(string clientId, string clientSecret, string isrc, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(isrc)) return null;
        var q = Uri.EscapeDataString($"isrc:{isrc}");
        var json = await api.GetJsonAsync(clientId, clientSecret, $"{ApiSearchUrl}?q={q}&type=track&limit=1", ct);
        return json is null ? null : SpotifyCatalogJson.ParseFirstTrackId(json);
    }

    private static string BuildSearchCacheKey(string query, int limit, string? market)
    {
        var raw = $"{limit}|{market ?? ""}|{query}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        return $"spotify_catalog_search:{hash}";
    }
}
