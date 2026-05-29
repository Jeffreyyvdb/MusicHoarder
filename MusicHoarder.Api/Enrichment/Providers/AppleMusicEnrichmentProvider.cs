using Microsoft.Extensions.Options;
using MusicHoarder.Api.AppleMusic;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment.Providers;

/// <summary>
/// Free, no-auth Apple/iTunes name-based enrichment provider. Search-only (iTunes carries no
/// ISRC), so it acts purely as a corroborating voter. Priority 350 so it runs after the
/// ISRC-bearing providers (Deezer 250 / Spotify 300).
/// </summary>
public class AppleMusicEnrichmentProvider(
    IAppleMusicCatalogService catalog,
    IOptions<MusicEnricherOptions> options,
    ILogger<AppleMusicEnrichmentProvider> logger) : IEnrichmentProvider
{
    private const double FuzzyThreshold = 85.0;

    // Shared scoring constants (mirror SpotifyApiEnrichmentProvider's tuned values).
    private const double DurationMismatchPenalty = 0.7;
    private const double VersionMismatchPenalty = 0.6;

    public string Name => "AppleMusic";
    public int Priority => 350;

    public bool CanHandle(SongMetadata song) =>
        SongSearchText.HasSearchableText(song, options.Value.SourceDirectory);

    public async Task<ProviderOutcome> TryEnrichAsync(SongMetadata song, CancellationToken ct = default)
    {
        var (effectiveArtist, effectiveTitle) = SongSearchText.Resolve(song, options.Value.SourceDirectory);
        if (string.IsNullOrWhiteSpace(effectiveArtist) || string.IsNullOrWhiteSpace(effectiveTitle))
        {
            logger.LogDebug("Apple Music enrichment: no searchable artist/title (SongId={SongId})", song.Id);
            return new ProviderNoMatch();
        }

        IReadOnlyList<AppleMusicCatalogTrack> tracks;
        try
        {
            var query = $"{effectiveArtist} {effectiveTitle}".Trim();
            tracks = await catalog.SearchTracksAsync(query, ct);
        }
        catch (ProviderRateLimitedException ex)
        {
            logger.LogWarning("Apple Music rate limited for song {SongId}, retry after {Delay}s",
                song.Id, ex.RetryAfter.TotalSeconds);
            return new ProviderRateLimited(ex.RetryAfter);
        }

        if (tracks.Count == 0)
        {
            logger.LogDebug("Apple Music search returned no tracks for SongId={SongId}", song.Id);
            return new ProviderNoMatch();
        }

        var opts = options.Value;
        AppleMusicCatalogTrack? bestTrack = null;
        double bestScore = 0;
        var bestWarnings = new List<string>();

        foreach (var track in tracks)
        {
            var (score, warnings) = ScoreCandidate(song, effectiveArtist, effectiveTitle, track, opts);
            if (score > bestScore)
            {
                bestScore = score;
                bestTrack = track;
                bestWarnings = warnings;
            }
        }

        if (bestTrack is null)
            return new ProviderNoMatch();

        if (bestScore < opts.AppleMusicApiMinConfidence - 1e-9)
            return new ProviderNoMatch(BuildResult(song, bestTrack, bestScore, bestWarnings, EnrichmentStatus.NeedsReview));

        var blocking = HasBlockingWarning(bestWarnings);
        var status = bestScore >= opts.AppleMusicApiMatchedThreshold - 1e-9 && !blocking
            ? EnrichmentStatus.Matched
            : EnrichmentStatus.NeedsReview;

        return new ProviderMatched(BuildResult(song, bestTrack, bestScore, bestWarnings, status));
    }

    private EnrichmentProviderResult BuildResult(
        SongMetadata song,
        AppleMusicCatalogTrack track,
        double score,
        List<string> warnings,
        EnrichmentStatus status)
    {
        var effectiveArtist = string.IsNullOrWhiteSpace(track.Artist) ? song.Artist : track.Artist;
        var albumArtist = ArtistCreditNormalizer.GetPrimaryArtist(effectiveArtist) ?? effectiveArtist;

        return new EnrichmentProviderResult(
            Artist: effectiveArtist,
            AlbumArtist: albumArtist,
            Title: string.IsNullOrWhiteSpace(track.Title) ? song.Title : track.Title,
            Year: track.ReleaseYear,
            TrackNumber: track.TrackNumber,
            MusicBrainzId: null,
            MusicBrainzReleaseId: null,
            SpotifyId: null,
            AcoustIdTrackId: null,
            Isrc: null,
            MatchedBy: Name,
            MatchConfidence: Math.Clamp(score, 0, 1),
            MatchWarnings: warnings,
            RecommendedStatus: status,
            Album: string.IsNullOrWhiteSpace(track.AlbumName) ? null : track.AlbumName,
            DurationSeconds: track.DurationMs > 0 ? track.DurationMs / 1000 : null);
    }

    private static (double Score, List<string> Warnings) ScoreCandidate(
        SongMetadata song,
        string? sourceArtist,
        string? sourceTitle,
        AppleMusicCatalogTrack track,
        MusicEnricherOptions opts)
    {
        var warnings = new List<string>();

        var artistRatio = FuzzyTextMatch.Ratio(sourceArtist, track.Artist);
        var titleRatio = FuzzyTextMatch.Ratio(sourceTitle, track.Title);

        if (artistRatio is double ar && ar < FuzzyThreshold)
            warnings.Add("artist_mismatch");
        if (titleRatio is double tr && tr < FuzzyThreshold)
            warnings.Add("title_mismatch");

        double score;
        if (artistRatio is double a && titleRatio is double t)
        {
            score = (a / 100.0 + t / 100.0) / 2.0;
        }
        else if (titleRatio is double tOnly)
        {
            score = tOnly / 100.0;
            warnings.Add("artist_unknown");
        }
        else
        {
            score = 0;
        }

        var songDurationSec = song.DurationSeconds
            ?? (song.DurationMs is int ms ? ms / 1000.0 : (double?)null);
        if (songDurationSec is not null && track.DurationMs > 0)
        {
            var delta = Math.Abs(songDurationSec.Value - track.DurationMs / 1000.0);
            if (delta > opts.AppleMusicApiDurationDeltaThresholdSeconds)
            {
                warnings.Add("duration_mismatch");
                score *= DurationMismatchPenalty;
            }
        }

        var sourceQual = VersionQualifier.Detect(song.Title, song.Album);
        var candQual = VersionQualifier.Detect(track.Title, track.AlbumName);
        if (!VersionQualifier.Compare(sourceQual, candQual))
        {
            warnings.Add("version_mismatch");
            score *= VersionMismatchPenalty;
        }

        return (Math.Clamp(score, 0, 1), warnings);
    }

    private static bool HasBlockingWarning(List<string> warnings) =>
        warnings.Exists(static w => w is "duration_mismatch" or "artist_mismatch" or "title_mismatch" or "version_mismatch" or "artist_unknown");
}
