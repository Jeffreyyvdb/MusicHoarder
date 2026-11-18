using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Discover;

/// <summary>
/// Reads the Spotify Playlist Archive's GitHub repos over HTTP and keeps what it read in memory. The
/// rules over the files are <see cref="ArchiveFormat"/>'s.
/// </summary>
public sealed class SpotifyPlaylistArchiveClient(
    HttpClient http,
    IMemoryCache cache,
    IOptions<MusicEnricherOptions> options,
    ILogger<SpotifyPlaylistArchiveClient> logger) : ISpotifyPlaylistArchive
{
    private const string CatalogKey = "spotify_archive:catalog";
    private const string OEmbedUrl = "https://open.spotify.com/oembed?url=https://open.spotify.com/playlist/";
    private static readonly TimeSpan FailureRetryAfter = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CoverTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan MissingCoverTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CoverTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim catalogGate = new(1, 1);
    private readonly SemaphoreSlim coverGate = new(6, 6);

    private sealed record CatalogEntry(IReadOnlyDictionary<string, ArchivedPlaylist>? Playlists);

    private sealed record CoverEntry(string? Url);

    public async Task<IReadOnlyList<ArchivedPlaylist>?> BrowseAsync(string? search, int limit, CancellationToken ct)
    {
        var catalog = await LoadCatalogAsync(ct);
        return catalog is null ? null : ArchiveFormat.Search(catalog.Values, search, limit);
    }

    public async Task<ArchivedPlaylist?> FindAsync(string playlistId, CancellationToken ct)
    {
        if (!ArchiveFormat.IsSpotifyId(playlistId))
            return null;
        var catalog = await LoadCatalogAsync(ct);
        return catalog is not null && catalog.TryGetValue(playlistId, out var playlist) ? playlist : null;
    }

    public async Task<ArchivedPlaylistTracks?> GetTracksAsync(string playlistId, CancellationToken ct)
    {
        var playlist = await FindAsync(playlistId, ct);
        if (playlist is null)
            return null;

        var cacheKey = $"spotify_archive:tracks:{playlist.Id}:{playlist.SnapshotId}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<ArchivedTrack>? cached) && cached is not null)
            return new ArchivedPlaylistTracks(playlist, cached);

        // The source that lists the playlist first, then the others newest first: a repo that stopped
        // updating can still hold a playlist the live one dropped, and the reverse.
        var candidates = new[] { playlist.SourceBaseUrl }
            .Concat(Sources().AsEnumerable().Reverse())
            .Distinct(StringComparer.Ordinal);

        foreach (var baseUrl in candidates)
        {
            var tracks = await TryFetchTracksAsync(baseUrl, playlist.Id, ct);
            if (tracks is null) continue;

            cache.Set(cacheKey, tracks, CacheTtl());
            return new ArchivedPlaylistTracks(playlist, tracks);
        }

        return null;
    }

    public async Task<string?> GetCoverUrlAsync(string playlistId, CancellationToken ct)
    {
        if (!ArchiveFormat.IsSpotifyId(playlistId))
            return null;

        var cacheKey = $"spotify_archive:cover:{playlistId}";
        if (cache.TryGetValue(cacheKey, out CoverEntry? cached) && cached is not null)
            return cached.Url;

        await coverGate.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CoverTimeout);
            var url = await FetchCoverAsync(playlistId, timeout.Token, ct);
            cache.Set(cacheKey, new CoverEntry(url), url is null ? MissingCoverTtl : CoverTtl);
            return url;
        }
        finally
        {
            coverGate.Release();
        }
    }

    private async Task<string?> FetchCoverAsync(string playlistId, CancellationToken attempt, CancellationToken caller)
    {
        try
        {
            using var response = await http.GetAsync($"{OEmbedUrl}{playlistId}", attempt);
            if (!response.IsSuccessStatusCode)
                return null;
            await using var body = await response.Content.ReadAsStreamAsync(attempt);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: attempt);
            return doc.RootElement.TryGetProperty("thumbnail_url", out var thumbnail)
                && thumbnail.ValueKind == JsonValueKind.String
                && Uri.TryCreate(thumbnail.GetString(), UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps
                    ? uri.ToString()
                    : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !caller.IsCancellationRequested)
        {
            logger.LogDebug(ex, "No Spotify oEmbed cover for playlist {PlaylistId}", playlistId);
            return null;
        }
    }

    private async Task<IReadOnlyList<ArchivedTrack>?> TryFetchTracksAsync(string baseUrl, string playlistId, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync($"{baseUrl}/playlists/pretty/{playlistId}.json", ct);
            if (!response.IsSuccessStatusCode)
                return null;
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            return ArchiveFormat.ParseTracks(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read playlist {PlaylistId} from archive source {Source}", playlistId, baseUrl);
            return null;
        }
    }

    private async Task<IReadOnlyDictionary<string, ArchivedPlaylist>?> LoadCatalogAsync(CancellationToken ct)
    {
        if (cache.TryGetValue(CatalogKey, out CatalogEntry? cached) && cached is not null)
            return cached.Playlists;

        await catalogGate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(CatalogKey, out cached) && cached is not null)
                return cached.Playlists;

            var parsed = new List<IReadOnlyList<ArchivedPlaylist>>();
            var failed = 0;
            foreach (var baseUrl in Sources())
            {
                var source = await TryFetchMetadataAsync(baseUrl, ct);
                if (source is null) failed++;
                else parsed.Add(source);
            }

            if (parsed.Count == 0)
            {
                cache.Set(CatalogKey, new CatalogEntry(null), FailureRetryAfter);
                return null;
            }

            var catalog = ArchiveFormat.Merge(parsed);
            cache.Set(CatalogKey, new CatalogEntry(catalog), failed == 0 ? CacheTtl() : FailureRetryAfter);
            return catalog;
        }
        finally
        {
            catalogGate.Release();
        }
    }

    private async Task<IReadOnlyList<ArchivedPlaylist>?> TryFetchMetadataAsync(string baseUrl, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync($"{baseUrl}/playlists/metadata/metadata-full.json.br", ct);
            response.EnsureSuccessStatusCode();
            await using var compressed = await response.Content.ReadAsStreamAsync(ct);
            await using var json = new BrotliStream(compressed, CompressionMode.Decompress);
            return ArchiveFormat.ParseMetadata(json, baseUrl);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read the Spotify playlist archive source {Source}", baseUrl);
            return null;
        }
    }

    private string[] Sources() =>
        options.Value.SpotifyPlaylistArchiveSources
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().TrimEnd('/'))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private TimeSpan CacheTtl() => TimeSpan.FromMinutes(Math.Max(1, options.Value.SpotifyPlaylistArchiveCacheMinutes));
}
