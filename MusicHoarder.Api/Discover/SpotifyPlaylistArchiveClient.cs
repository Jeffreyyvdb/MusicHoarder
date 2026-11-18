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

    /// <summary>The repos read when <see cref="MusicEnricherOptions.SpotifyPlaylistArchiveSources"/> is empty, oldest first.</summary>
    public static readonly string[] DefaultSources =
    [
        "https://raw.githubusercontent.com/mackorone/spotify-playlist-archive/main",
        "https://raw.githubusercontent.com/mackorone/spotify-playlist-archive-2/main",
    ];

    /// <param name="NewestFailedSource">
    /// Index (in <see cref="Sources"/>) of the newest source that could not be read this time, or -1. A
    /// playlist an older source supplied may have a fresher copy there, so its tracklist is not trusted.
    /// </param>
    private sealed record CatalogEntry(IReadOnlyDictionary<string, ArchivedPlaylist>? Playlists, int NewestFailedSource);

    private abstract record SourceRead
    {
        public sealed record Read(IReadOnlyList<ArchivedPlaylist> Playlists) : SourceRead;
        public sealed record Gone : SourceRead;
        public sealed record Failed : SourceRead;
    }

    private sealed record CoverEntry(string? Url);

    public async Task<IReadOnlyList<ArchivedPlaylist>?> BrowseAsync(string? search, int limit, CancellationToken ct)
    {
        var catalog = (await LoadCatalogAsync(ct)).Playlists;
        return catalog is null ? null : ArchiveFormat.Search(catalog.Values, search, limit);
    }

    public async Task<ArchivedPlaylist?> FindAsync(string playlistId, CancellationToken ct)
    {
        if (!ArchiveFormat.IsSpotifyId(playlistId))
            return null;
        var catalog = (await LoadCatalogAsync(ct)).Playlists;
        return catalog is not null && catalog.TryGetValue(playlistId, out var playlist) ? playlist : null;
    }

    public async Task<ArchivedPlaylistTracks?> GetTracksAsync(string playlistId, CancellationToken ct)
    {
        if (!ArchiveFormat.IsSpotifyId(playlistId))
            return null;
        var entry = await LoadCatalogAsync(ct);
        if (entry.Playlists is null || !entry.Playlists.TryGetValue(playlistId, out var playlist))
            return null;
        // A subscription would wishlist the stale copy's tracks and record them as the playlist's current list.
        if (Array.IndexOf(Sources(), playlist.SourceBaseUrl) < entry.NewestFailedSource)
            return null;

        var cacheKey = $"spotify_archive:tracks:{playlist.Id}:{playlist.SnapshotId}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<ArchivedTrack>? cached) && cached is not null)
            return new ArchivedPlaylistTracks(playlist, cached);

        // Only the source whose snapshot the catalog holds: an older repo's copy can be months stale, and a
        // subscription would wishlist tracks that left the playlist long ago and record them as current.
        var tracks = await TryFetchTracksAsync(playlist.SourceBaseUrl, playlist.Id, ct);
        if (tracks is null)
            return null;

        cache.Set(cacheKey, tracks, CacheTtl());
        return new ArchivedPlaylistTracks(playlist, tracks);
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

    private async Task<CatalogEntry> LoadCatalogAsync(CancellationToken ct)
    {
        if (cache.TryGetValue(CatalogKey, out CatalogEntry? cached) && cached is not null)
            return cached;

        await catalogGate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(CatalogKey, out cached) && cached is not null)
                return cached;

            var sources = Sources();
            var parsed = new List<IReadOnlyList<ArchivedPlaylist>>();
            var newestFailed = -1;
            for (var i = 0; i < sources.Length; i++)
            {
                switch (await TryFetchMetadataAsync(sources[i], ct))
                {
                    case SourceRead.Read read: parsed.Add(read.Playlists); break;
                    case SourceRead.Failed: newestFailed = i; break;
                }
            }

            var entry = parsed.Count == 0 && newestFailed >= 0
                ? new CatalogEntry(null, newestFailed)
                : new CatalogEntry(ArchiveFormat.Merge(parsed), newestFailed);
            cache.Set(CatalogKey, entry, newestFailed >= 0 ? FailureRetryAfter : CacheTtl());
            return entry;
        }
        finally
        {
            catalogGate.Release();
        }
    }

    private async Task<SourceRead> TryFetchMetadataAsync(string baseUrl, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync($"{baseUrl}/playlists/metadata/metadata-full.json.br", ct);
            // A repo that was deleted or renamed answers 404 for good: it holds nothing, which is not a failure
            // worth retrying every minute.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                logger.LogWarning("Spotify playlist archive source {Source} has no metadata file (404); skipping it", baseUrl);
                return new SourceRead.Gone();
            }
            response.EnsureSuccessStatusCode();
            await using var compressed = await response.Content.ReadAsStreamAsync(ct);
            await using var json = new BrotliStream(compressed, CompressionMode.Decompress);
            return new SourceRead.Read(ArchiveFormat.ParseMetadata(json, baseUrl));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read the Spotify playlist archive source {Source}", baseUrl);
            return new SourceRead.Failed();
        }
    }

    private string[] Sources() =>
        (options.Value.SpotifyPlaylistArchiveSources is { Length: > 0 } configured ? configured : DefaultSources)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().TrimEnd('/'))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private TimeSpan CacheTtl() => TimeSpan.FromMinutes(Math.Max(1, options.Value.SpotifyPlaylistArchiveCacheMinutes));
}
