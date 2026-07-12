using Microsoft.Extensions.Options;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Spotify;
using MusicHoarder.Api.StreamingFlac;

namespace MusicHoarder.Api.Download;

/// <summary>
/// Acquires true-lossless FLAC by delegating to an optional, self-hosted acquisition sidecar (see
/// <see cref="StreamingFlacSidecarClient"/>). The sidecar takes a Spotify track URL and writes a single
/// FLAC into the shared staging dir; this provider just resolves the URL, hands it off, and reports the
/// file path back. Acquisition only — MusicHoarder's own enrichment/tagging owns all metadata, so the
/// downloaded file carries no sidecar-written tags (the caller stamps the known Spotify identity).
/// <para>
/// Falls through (<see cref="DownloadResult.Missing"/>) when the sidecar is unconfigured, no Spotify id
/// can be resolved, or the track has no lossless source upstream — so the wishlist chain drops to slskd
/// / yt-dlp. Only a transport-level sidecar failure returns <see cref="DownloadResult.Failed"/>, which
/// stops the chain (a downed sidecar shouldn't silently burn the fallback's quota on every track).
/// </para>
/// </summary>
public sealed class StreamingFlacDownloadProvider(
    StreamingFlacSidecarClient sidecar,
    ISpotifyCatalogSearchService catalogSearch,
    IOptions<SpotifyOptions> spotifyOptions,
    IOptionsMonitor<StreamingFlacOptions> options,
    ILogger<StreamingFlacDownloadProvider> logger) : IDownloadProvider
{
    public string Name => "spotiflac";

    public async Task<DownloadResult> DownloadAsync(DownloadRequest req, CancellationToken ct)
    {
        if (!options.CurrentValue.IsConfigured)
            return DownloadResult.Missing("streaming-flac sidecar not configured");

        try
        {
            var spotifyUrl = await ResolveSpotifyUrlAsync(req, ct);
            if (spotifyUrl is null)
            {
                logger.LogInformation("streaming-flac: no resolvable Spotify id for '{Artist} - {Title}'",
                    LogSanitizer.ForLog(req.Artist), LogSanitizer.ForLog(req.Title));
                return DownloadResult.Missing("no resolvable Spotify id");
            }

            Directory.CreateDirectory(req.DestinationDirectory);
            var stem = Guid.NewGuid().ToString("N");

            var result = await sidecar.AcquireAsync(spotifyUrl, req.DestinationDirectory, stem, ct);
            switch (result.Status)
            {
                case AcquireStatus.Ok:
                    // The sidecar reports the path it wrote; verify it actually landed on our side of
                    // the shared volume (a missing/empty file means a volume misconfig, not a real
                    // download — treat as a transient failure so it isn't marked Downloaded).
                    if (result.File is not { } file || !File.Exists(file) || new FileInfo(file).Length == 0)
                    {
                        logger.LogWarning("streaming-flac reported ok but file is missing/empty: {File}",
                            LogSanitizer.ForLog(result.File ?? "(null)"));
                        return DownloadResult.Failed("sidecar reported ok but no file was produced");
                    }
                    logger.LogInformation("streaming-flac acquired '{Artist} - {Title}' via {Provider} ({Size} bytes)",
                        LogSanitizer.ForLog(req.Artist), LogSanitizer.ForLog(req.Title),
                        LogSanitizer.ForLog(result.Provider ?? "?"), new FileInfo(file).Length);
                    return DownloadResult.Ok(file);

                case AcquireStatus.NotFound:
                    logger.LogInformation("streaming-flac found no lossless source for '{Artist} - {Title}': {Error}",
                        LogSanitizer.ForLog(req.Artist), LogSanitizer.ForLog(req.Title),
                        LogSanitizer.ForLog(result.Error ?? ""));
                    return DownloadResult.Missing(result.Error ?? "no lossless source");

                default:
                    return DownloadResult.Failed(result.Error ?? "sidecar error");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "streaming-flac download failed for '{Artist} - {Title}'",
                LogSanitizer.ForLog(req.Artist), LogSanitizer.ForLog(req.Title));
            return DownloadResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Builds the Spotify track URL the sidecar needs. Prefers the wishlist item's own
    /// <see cref="DownloadRequest.SpotifyTrackId"/>; when absent, resolves the ISRC → track id via the
    /// Spotify catalog client. Credential resolution stays on the C# side (the sidecar never learns to
    /// resolve ISRCs), and uses <see cref="SpotifyOptions"/> only — the download runs in a DB-free
    /// section, so a DB-only Spotify config just skips this rare ISRC-fallback path.
    /// </summary>
    private async Task<string?> ResolveSpotifyUrlAsync(DownloadRequest req, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(req.SpotifyTrackId))
            return TrackUrl(req.SpotifyTrackId!);

        var isrc = ProviderIdentity.NormalizeIsrc(req.Isrc);
        if (string.IsNullOrEmpty(isrc))
            return null;

        var opts = spotifyOptions.Value;
        var clientId = opts.ClientId?.Trim();
        var clientSecret = opts.ClientSecret?.Trim();
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            return null;

        var id = await catalogSearch.SearchTrackIdByIsrcAsync(clientId!, clientSecret!, isrc, ct);
        return string.IsNullOrWhiteSpace(id) ? null : TrackUrl(id!);
    }

    private static string TrackUrl(string trackId) => $"https://open.spotify.com/track/{trackId}";
}
