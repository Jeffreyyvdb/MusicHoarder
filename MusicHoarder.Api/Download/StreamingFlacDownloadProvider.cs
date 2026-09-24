using Microsoft.Extensions.Options;
using MusicHoarder.Api.Audio;
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
/// The sidecar fetches exactly the edition the Spotify id names, so with
/// <see cref="StreamingFlacOptions.PreferExplicit"/> on a clean edit is first swapped for its explicit
/// edition when Spotify has one (<see cref="ExplicitVersionMatcher"/>); the result then carries that
/// edition's ISRC so the file isn't stamped with the clean one.
/// </para>
/// <para>
/// Falls through (<see cref="DownloadResult.Missing"/>) when the sidecar is unconfigured, no Spotify id
/// can be resolved, or the track has no lossless source upstream — so the wishlist chain drops to slskd
/// / yt-dlp. A sidecar that cannot be reached at all (DNS / connection refused — down or mid-redeploy)
/// returns <see cref="DownloadResult.ProviderUnavailable"/>, which also falls through but marks the
/// item so it is offered to this provider again once it is back. Only a failure from a reachable
/// sidecar (timeout, 5xx, empty file) returns <see cref="DownloadResult.Failed"/>, which stops the
/// chain so a flaky sidecar doesn't silently burn the fallback's quota on every track.
/// </para>
/// </summary>
public sealed class StreamingFlacDownloadProvider(
    StreamingFlacSidecarClient sidecar,
    ISpotifyCatalogSearchService catalogSearch,
    ISpotifyAppCredentialsProvider spotifyCredentials,
    IOptionsMonitor<StreamingFlacOptions> options,
    ILogger<StreamingFlacDownloadProvider> logger) : IDownloadProvider, IUpgradeProvider
{
    public string Name => "spotiflac";

    /// <summary>The sidecar only ever produces lossless FLAC, so it can upgrade a lossy target but can't
    /// improve on one that's already lossless — skip those rather than download a same-tier file the
    /// merge would reject.</summary>
    public bool CanUpgrade(UpgradeFloor floor) =>
        options.CurrentValue.IsConfigured && floor.Tier < AudioCodecTier.Lossless;

    /// <summary>Acquisition is identical to a wishlist download (the sidecar yields a verified FLAC);
    /// the merge stage confirms it actually beats the target and is the same recording.</summary>
    public Task<DownloadResult> DownloadBetterAsync(DownloadRequest req, UpgradeFloor floor, CancellationToken ct) =>
        DownloadAsync(req, ct);

    public async Task<DownloadResult> DownloadAsync(DownloadRequest req, CancellationToken ct)
    {
        if (!options.CurrentValue.IsConfigured)
            return DownloadResult.Missing("streaming-flac sidecar not configured");

        try
        {
            var (clientId, clientSecret) = await spotifyCredentials.ResolveAsync(ct);
            var trackId = await ResolveTrackIdAsync(req, clientId, clientSecret, ct);
            if (trackId is null)
            {
                logger.LogInformation("streaming-flac: no resolvable Spotify id for '{Artist} - {Title}'",
                    LogSanitizer.ForLog(req.Artist), LogSanitizer.ForLog(req.Title));
                return DownloadResult.Missing("no resolvable Spotify id");
            }

            string? explicitIsrc = null;
            if (options.CurrentValue.PreferExplicit)
                (trackId, explicitIsrc) = await PreferExplicitEditionAsync(trackId, clientId, clientSecret, ct);

            Directory.CreateDirectory(req.DestinationDirectory);
            var stem = Guid.NewGuid().ToString("N");

            var result = await sidecar.AcquireAsync(TrackUrl(trackId), req.DestinationDirectory, stem, ct);
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
                    return DownloadResult.Ok(file) with { Isrc = explicitIsrc };

                case AcquireStatus.NotFound:
                    logger.LogInformation("streaming-flac found no lossless source for '{Artist} - {Title}': {Error}",
                        LogSanitizer.ForLog(req.Artist), LogSanitizer.ForLog(req.Title),
                        LogSanitizer.ForLog(result.Error ?? ""));
                    return DownloadResult.Missing(result.Error ?? "no lossless source");

                case AcquireStatus.Unavailable:
                    return DownloadResult.ProviderUnavailable(result.Error ?? "sidecar unreachable");

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
    /// The Spotify track id the sidecar's URL is built from. Prefers the wishlist item's own
    /// <see cref="DownloadRequest.SpotifyTrackId"/>; when absent, resolves the ISRC → track id via the
    /// Spotify catalog client. Credential resolution stays on the C# side (the sidecar never learns to
    /// resolve ISRCs). The credentials come from <see cref="ISpotifyAppCredentialsProvider"/>, which
    /// reads through its own DbContext scope, so the download's DB-free parallel section stays EF-safe.
    /// </summary>
    private async Task<string?> ResolveTrackIdAsync(
        DownloadRequest req, string? clientId, string? clientSecret, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(req.SpotifyTrackId))
            return req.SpotifyTrackId!;

        var isrc = ProviderIdentity.NormalizeIsrc(req.Isrc);
        if (string.IsNullOrEmpty(isrc))
            return null;

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            return null;

        var id = await catalogSearch.SearchTrackIdByIsrcAsync(clientId!, clientSecret!, isrc, ct);
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    /// <summary>
    /// Swaps a clean edit's id for its explicit edition's, returning that edition's ISRC alongside.
    /// Best-effort: without credentials, when the track is already explicit or has no explicit
    /// edition, or on any lookup failure (a rate limit included), the requested id is kept and the
    /// download goes ahead unchanged.
    /// </summary>
    private async Task<(string TrackId, string? Isrc)> PreferExplicitEditionAsync(
        string trackId, string? clientId, string? clientSecret, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            return (trackId, null);

        try
        {
            var track = await catalogSearch.GetTrackAsync(clientId!, clientSecret!, trackId, ct);
            if (track is null || track.Explicit)
                return (trackId, null);

            var candidates = await catalogSearch.SearchTracksAsync(
                clientId!, clientSecret!, ExplicitVersionMatcher.SearchQuery(track), ct);
            if (ExplicitVersionMatcher.FindExplicitEdition(track, candidates) is not { } edition)
                return (trackId, null);

            logger.LogInformation(
                "streaming-flac: '{Artist} - {Title}' ({TrackId}) is a clean edit; acquiring its explicit edition {ExplicitId}",
                LogSanitizer.ForLog(track.Artist), LogSanitizer.ForLog(track.Title),
                LogSanitizer.ForLog(trackId), LogSanitizer.ForLog(edition.Id));
            return (edition.Id, ProviderIdentity.NormalizeIsrc(edition.Isrc) is { Length: > 0 } isrc ? isrc : null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "streaming-flac: explicit-edition lookup failed for {TrackId}; using it as is",
                LogSanitizer.ForLog(trackId));
            return (trackId, null);
        }
    }

    private static string TrackUrl(string trackId) => $"https://open.spotify.com/track/{trackId}";
}
