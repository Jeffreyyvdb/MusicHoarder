using FuzzySharp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Enrichment.Providers;

public class SpotifyApiEnrichmentProvider(
    IServiceScopeFactory scopeFactory,
    ISpotifyCatalogSearchService catalogSearch,
    IOptions<MusicEnricherOptions> options,
    IOptions<SpotifyOptions> spotifyOptions,
    ILogger<SpotifyApiEnrichmentProvider> logger) : IEnrichmentProvider
{
    private const double FuzzyThreshold = 85.0;

    public string Name => "SpotifyAPI";
    public int Priority => 300;

    public bool CanHandle(SongMetadata song) =>
        !string.IsNullOrWhiteSpace(song.Artist) && !string.IsNullOrWhiteSpace(song.Title);

    public async Task<ProviderOutcome> TryEnrichAsync(SongMetadata song, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var settings = await db.SpotifySettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var (clientId, clientSecret) = SpotifyAppCredentialsResolver.Resolve(settings, spotifyOptions.Value);

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            logger.LogDebug("Spotify API enrichment skipped: no ClientId/ClientSecret (SongId={SongId})", song.Id);
            return new ProviderNoMatch();
        }

        var query = BuildSearchQuery(song.Artist!, song.Title!);
        if (string.IsNullOrWhiteSpace(query))
        {
            logger.LogDebug("Spotify API enrichment: empty query after normalize (SongId={SongId})", song.Id);
            return new ProviderNoMatch();
        }

        IReadOnlyList<SpotifyCatalogTrack> tracks;
        try
        {
            tracks = await catalogSearch.SearchTracksAsync(clientId, clientSecret, query, ct);
        }
        catch (ProviderRateLimitedException ex)
        {
            logger.LogWarning("Spotify rate limited for song {SongId}, retry after {Delay}s",
                song.Id, ex.RetryAfter.TotalSeconds);
            return new ProviderRateLimited(ex.RetryAfter);
        }

        if (tracks.Count == 0)
        {
            logger.LogDebug("Spotify search returned no tracks for SongId={SongId}", song.Id);
            return new ProviderNoMatch();
        }

        var opts = options.Value;
        SpotifyCatalogTrack? bestTrack = null;
        double bestScore = 0;
        var bestWarnings = new List<string>();

        foreach (var track in tracks)
        {
            var (score, warnings) = ScoreCandidate(song, track, opts);
            if (score > bestScore)
            {
                bestScore = score;
                bestTrack = track;
                bestWarnings = warnings;
            }
        }

        if (bestTrack is null || bestScore < opts.SpotifyApiMinConfidence - 1e-9)
            return new ProviderNoMatch();

        var blocking = HasBlockingWarning(bestWarnings);
        var status = bestScore >= opts.SpotifyApiMatchedThreshold - 1e-9 && !blocking
            ? EnrichmentStatus.Matched
            : EnrichmentStatus.NeedsReview;

        var effectiveArtist = string.IsNullOrWhiteSpace(bestTrack.Artist) ? song.Artist : bestTrack.Artist;
        var albumArtist = ArtistCreditNormalizer.GetPrimaryArtist(effectiveArtist) ?? effectiveArtist;

        return new ProviderMatched(new EnrichmentProviderResult(
            Artist: effectiveArtist,
            AlbumArtist: albumArtist,
            Title: string.IsNullOrWhiteSpace(bestTrack.Title) ? song.Title : bestTrack.Title,
            Year: bestTrack.ReleaseYear,
            TrackNumber: bestTrack.TrackNumber,
            MusicBrainzId: null,
            MusicBrainzReleaseId: null,
            SpotifyId: bestTrack.Id,
            AcoustIdTrackId: null,
            Isrc: string.IsNullOrWhiteSpace(bestTrack.Isrc) ? null : NormalizeIsrc(bestTrack.Isrc),
            MatchedBy: Name,
            MatchConfidence: Math.Clamp(bestScore, 0, 1),
            MatchWarnings: bestWarnings,
            RecommendedStatus: status,
            Album: string.IsNullOrWhiteSpace(bestTrack.AlbumName) ? null : bestTrack.AlbumName));
    }

    private static string BuildSearchQuery(string artist, string title)
    {
        var combined = $"{artist} {title}";
        return SpotifyLibraryComparisonService.Normalize(combined);
    }

    private static (double Score, List<string> Warnings) ScoreCandidate(
        SongMetadata song,
        SpotifyCatalogTrack track,
        MusicEnricherOptions opts)
    {
        var warnings = new List<string>();

        var songArtistNorm = SpotifyLibraryComparisonService.Normalize(song.Artist);
        var songTitleNorm = SpotifyLibraryComparisonService.Normalize(song.Title);
        var candArtistNorm = SpotifyLibraryComparisonService.Normalize(track.Artist);
        var candTitleNorm = SpotifyLibraryComparisonService.Normalize(track.Title);

        var artistRatio = string.IsNullOrEmpty(songArtistNorm) || string.IsNullOrEmpty(candArtistNorm)
            ? 100.0
            : Fuzz.WeightedRatio(songArtistNorm, candArtistNorm);
        var titleRatio = string.IsNullOrEmpty(songTitleNorm) || string.IsNullOrEmpty(candTitleNorm)
            ? 100.0
            : Fuzz.WeightedRatio(songTitleNorm, candTitleNorm);

        if (artistRatio < FuzzyThreshold)
            warnings.Add("artist_mismatch");
        if (titleRatio < FuzzyThreshold)
            warnings.Add("title_mismatch");

        var score = (artistRatio / 100.0 + titleRatio / 100.0) / 2.0;

        var fileIsrc = NormalizeIsrc(song.Isrc);
        var trackIsrc = NormalizeIsrc(track.Isrc);
        if (!string.IsNullOrEmpty(fileIsrc))
        {
            if (!string.IsNullOrEmpty(trackIsrc))
            {
                if (string.Equals(fileIsrc, trackIsrc, StringComparison.Ordinal))
                    score = Math.Min(1.0, score + opts.SpotifyApiIsrcConfidenceBoost);
                else
                {
                    warnings.Add("isrc_mismatch");
                    score *= 0.65;
                }
            }
            else
            {
                warnings.Add("isrc_not_on_spotify_track");
            }
        }

        var songDurationSec = song.DurationSeconds
            ?? (song.DurationMs is int ms ? ms / 1000.0 : (double?)null);
        if (songDurationSec is not null && track.DurationMs > 0)
        {
            var delta = Math.Abs(songDurationSec.Value - track.DurationMs / 1000.0);
            if (delta > opts.SpotifyApiDurationDeltaThresholdSeconds)
            {
                warnings.Add("duration_mismatch");
                score *= opts.SpotifyApiDurationMismatchPenalty;
            }
        }

        return (Math.Clamp(score, 0, 1), warnings);
    }

    private static bool HasBlockingWarning(List<string> warnings) =>
        warnings.Exists(static w => w is "duration_mismatch" or "artist_mismatch" or "title_mismatch" or "isrc_mismatch");

    private static string NormalizeIsrc(string? isrc)
    {
        if (string.IsNullOrWhiteSpace(isrc))
            return string.Empty;
        return isrc.Trim().ToUpperInvariant().Replace("-", "", StringComparison.Ordinal);
    }
}
