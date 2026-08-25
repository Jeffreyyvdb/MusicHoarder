using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Enrichment.AlbumTracklist;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Download;

/// <summary>
/// Gives an album-completion track the identifier its downloader needs. A
/// <see cref="CanonicalAlbumTrack"/> is reconciled from several providers and carries no streaming id,
/// so a queued fill item would reach the chain with nothing but artist/title — and the lossless
/// provider (<see cref="StreamingFlacDownloadProvider"/>) needs a Spotify track id or an ISRC to
/// acquire anything. Without one it reports "not found" and every filled track lands on the yt-dlp
/// floor as lossy Opus, which is exactly what album completion looked like before this existed.
/// </summary>
public interface IAlbumCompletionIdentityResolver
{
    /// <summary>
    /// Spotify track ids for <paramref name="tracks"/>, keyed by <see cref="CanonicalAlbumTrack.Id"/>.
    /// Tracks with no confident counterpart are simply absent — the caller queues them anyway and the
    /// chain falls through to its lossy floor, exactly as before.
    /// </summary>
    Task<IReadOnlyDictionary<int, string>> ResolveSpotifyTrackIdsAsync(
        CanonicalAlbum album, IReadOnlyList<CanonicalAlbumTrack> tracks, CancellationToken ct);
}

/// <summary>
/// Resolves the ids from the Spotify Web API, one album lookup per swept album: the album id recorded
/// by the Spotify tracklist provider when it corroborated this album (free — it is already in
/// <see cref="CanonicalAlbum.SourcesJson"/>), else an artist+album search. Tracks are then paired with
/// <see cref="AlbumOwnedTrackMatcher"/>, the same position-then-fuzzy-title ladder the album view uses
/// to decide what is missing, so both sides agree on what a track *is*.
/// </summary>
public sealed class SpotifyAlbumCompletionIdentityResolver(
    MusicHoarderDbContext db,
    ISpotifyCatalogSearchService catalog,
    IOwnerLookupService ownerLookup,
    IOptions<SpotifyOptions> spotifyOptions,
    IOptions<MusicEnricherOptions> enricherOptions,
    ILogger<SpotifyAlbumCompletionIdentityResolver> logger) : IAlbumCompletionIdentityResolver
{
    /// <summary>
    /// Two recordings that share a slot and a title but differ by more than this are not the same take
    /// (radio edit, live version, a search that landed on the wrong edition). Generous on purpose: the
    /// cost of dropping a good id is one lossy download, the cost of keeping a bad one is the wrong song.
    /// </summary>
    private const int DurationToleranceMs = 10_000;

    private static readonly IReadOnlyDictionary<int, string> None = new Dictionary<int, string>();

    public async Task<IReadOnlyDictionary<int, string>> ResolveSpotifyTrackIdsAsync(
        CanonicalAlbum album, IReadOnlyList<CanonicalAlbumTrack> tracks, CancellationToken ct)
    {
        if (tracks.Count == 0)
            return None;

        var opts = enricherOptions.Value;
        if (!opts.EnableSpotifyApiProvider)
            return None;

        var (clientId, clientSecret) = await ResolveCredentialsAsync(ct);
        if (clientId is null || clientSecret is null)
        {
            logger.LogDebug("Album completion: no Spotify app credentials, filled tracks stay unidentified");
            return None;
        }

        try
        {
            var albumId = SpotifyAlbumIdFromSources(album.SourcesJson)
                ?? await catalog.SearchAlbumIdAsync(
                    clientId, clientSecret, album.DisplayArtist ?? "", album.DisplayTitle ?? "", ct);
            if (albumId is null)
                return None;

            var detail = await catalog.GetAlbumAsync(clientId, clientSecret, albumId, ct);
            if (detail is null || detail.Tracks.Count == 0)
                return None;

            // The matcher speaks in owned songs, so the Spotify tracklist stands in for one: its list
            // index is the surrogate id, resolved back to the real track id below.
            var indexed = detail.Tracks
                .Select((t, i) => new OwnedTrackInfo(i, null, t.DiscNumber, t.TrackNumber, t.Title))
                .ToList();
            var paired = AlbumOwnedTrackMatcher.Match(tracks, indexed, opts.IdentityTitleThreshold);

            var resolved = new Dictionary<int, string>(paired.Count);
            foreach (var (canonicalTrackId, index) in paired)
            {
                var spotifyTrack = detail.Tracks[index];
                if (string.IsNullOrWhiteSpace(spotifyTrack.Id))
                    continue;

                var canonicalDuration = tracks.First(t => t.Id == canonicalTrackId).DurationMs;
                if (canonicalDuration is > 0 && spotifyTrack.DurationMs > 0
                    && Math.Abs(canonicalDuration.Value - spotifyTrack.DurationMs) > DurationToleranceMs)
                {
                    logger.LogDebug(
                        "Album completion: dropping Spotify id for '{Title}' — {CanonicalMs}ms vs {SpotifyMs}ms",
                        LogSanitizer.ForLog(spotifyTrack.Title ?? ""), canonicalDuration, spotifyTrack.DurationMs);
                    continue;
                }

                resolved[canonicalTrackId] = spotifyTrack.Id!;
            }

            logger.LogDebug(
                "Album completion: resolved {Resolved}/{Requested} Spotify track id(s) for '{Album}'",
                resolved.Count, tracks.Count, LogSanitizer.ForLog(album.DisplayTitle ?? album.AlbumKey));
            return resolved;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // An unidentified fill item still downloads (lossy), so a Spotify hiccup must not sink the
            // sweep — it only costs quality on the tracks queued in this pass.
            logger.LogWarning(ex, "Album completion: Spotify identity lookup failed for '{Album}'",
                LogSanitizer.ForLog(album.DisplayTitle ?? album.AlbumKey));
            return None;
        }
    }

    /// <summary>
    /// The Spotify album id already paid for during reconciliation, if that provider backed the winning
    /// cluster. A losing-cluster source is a different edition, so it is deliberately not reused.
    /// </summary>
    private string? SpotifyAlbumIdFromSources(string? sourcesJson)
    {
        if (string.IsNullOrWhiteSpace(sourcesJson))
            return null;

        try
        {
            var sources = JsonSerializer.Deserialize<List<AlbumTracklistReconciler.ReconciledSource>>(sourcesJson);
            return sources?
                .FirstOrDefault(s => s.Provider == EnrichmentProvider.SpotifyAPI
                    && s.InWinningCluster
                    && !string.IsNullOrWhiteSpace(s.AlbumId))?
                .AlbumId;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<(string? ClientId, string? ClientSecret)> ResolveCredentialsAsync(CancellationToken ct)
    {
        var settings = await db.SpotifySettings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.OwnerUserId == ownerLookup.OwnerUserId, ct);
        return SpotifyAppCredentialsResolver.Resolve(settings, spotifyOptions.Value);
    }
}
