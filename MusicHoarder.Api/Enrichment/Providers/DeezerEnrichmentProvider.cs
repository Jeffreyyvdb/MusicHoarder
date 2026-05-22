using Microsoft.Extensions.Options;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment.Providers;

/// <summary>
/// Free, no-auth Deezer name-based enrichment provider. ISRC-first (Deezer carries ISRC, so it
/// can seed the orchestrator's identifier-gossip chain) then fuzzy artist+title search. Priority
/// 250 so it runs before Spotify (300) / Apple (350) and any ISRC it discovers gossips forward.
/// </summary>
public class DeezerEnrichmentProvider(
    IDeezerCatalogService catalog,
    IOptions<MusicEnricherOptions> options,
    ILogger<DeezerEnrichmentProvider> logger) : IEnrichmentProvider
{
    private const double FuzzyThreshold = 85.0;

    // Shared scoring constants (mirror SpotifyApiEnrichmentProvider's tuned values).
    private const double IsrcConfidenceBoost = 0.12;
    private const double IsrcMismatchPenalty = 0.65;
    private const double DurationMismatchPenalty = 0.7;
    private const double VersionMismatchPenalty = 0.6;

    public string Name => "Deezer";
    public int Priority => 250;

    public bool CanHandle(SongMetadata song) =>
        SongSearchText.HasSearchableText(song, options.Value.SourceDirectory);

    public async Task<ProviderOutcome> TryEnrichAsync(SongMetadata song, CancellationToken ct = default)
    {
        var (effectiveArtist, effectiveTitle) = SongSearchText.Resolve(song, options.Value.SourceDirectory);
        if (string.IsNullOrWhiteSpace(effectiveArtist) || string.IsNullOrWhiteSpace(effectiveTitle))
        {
            logger.LogDebug("Deezer enrichment: no searchable artist/title (SongId={SongId})", song.Id);
            return new ProviderNoMatch();
        }

        var opts = options.Value;
        DeezerCatalogTrack? bestTrack;
        double bestScore;
        List<string> bestWarnings;

        try
        {
            // Identifier-first: if the file already carries an ISRC, ask Deezer for that exact
            // recording (full detail) before falling back to a fuzzy artist+title search.
            var fileIsrc = NormalizeIsrc(song.Isrc);
            if (!string.IsNullOrEmpty(fileIsrc))
            {
                var isrcTrack = await catalog.LookupByIsrcAsync(fileIsrc, ct);
                if (isrcTrack is not null)
                {
                    logger.LogDebug("Deezer ISRC lookup hit for SongId={SongId} ({Isrc})", song.Id, fileIsrc);
                    var (isrcScore, isrcWarnings) = ScoreCandidate(song, effectiveArtist, effectiveTitle, isrcTrack, opts);
                    return Finalize(song, isrcTrack, isrcScore, isrcWarnings, opts);
                }
            }

            var query = $"{effectiveArtist} {effectiveTitle}".Trim();
            var tracks = await catalog.SearchTracksAsync(query, ct);
            if (tracks.Count == 0)
            {
                logger.LogDebug("Deezer search returned no tracks for SongId={SongId}", song.Id);
                return new ProviderNoMatch();
            }

            bestTrack = null;
            bestScore = 0;
            bestWarnings = [];
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

            // Hydrate only the chosen candidate to get ISRC (for gossip) + release year / track #,
            // which Deezer omits from search results.
            if (string.IsNullOrEmpty(bestTrack.Isrc))
            {
                var hydrated = await catalog.LookupByIdAsync(bestTrack.Id, ct);
                if (hydrated is not null)
                    bestTrack = hydrated;
            }
        }
        catch (ProviderRateLimitedException ex)
        {
            logger.LogWarning("Deezer rate limited for song {SongId}, retry after {Delay}s",
                song.Id, ex.RetryAfter.TotalSeconds);
            return new ProviderRateLimited(ex.RetryAfter);
        }

        // Re-score against the hydrated track so an ISRC match can boost confidence.
        (bestScore, bestWarnings) = ScoreCandidate(song, effectiveArtist, effectiveTitle, bestTrack, opts);
        return Finalize(song, bestTrack, bestScore, bestWarnings, opts);
    }

    private ProviderOutcome Finalize(
        SongMetadata song, DeezerCatalogTrack track, double score, List<string> warnings, MusicEnricherOptions opts)
    {
        if (score < opts.DeezerApiMinConfidence - 1e-9)
            return new ProviderNoMatch(BuildResult(song, track, score, warnings, EnrichmentStatus.NeedsReview));

        var blocking = HasBlockingWarning(warnings);
        var status = score >= opts.DeezerApiMatchedThreshold - 1e-9 && !blocking
            ? EnrichmentStatus.Matched
            : EnrichmentStatus.NeedsReview;

        return new ProviderMatched(BuildResult(song, track, score, warnings, status));
    }

    private EnrichmentProviderResult BuildResult(
        SongMetadata song,
        DeezerCatalogTrack track,
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
            Isrc: string.IsNullOrWhiteSpace(track.Isrc) ? null : NormalizeIsrc(track.Isrc),
            MatchedBy: Name,
            MatchConfidence: Math.Clamp(score, 0, 1),
            MatchWarnings: warnings,
            RecommendedStatus: status,
            Album: string.IsNullOrWhiteSpace(track.AlbumName) ? null : track.AlbumName);
    }

    private static (double Score, List<string> Warnings) ScoreCandidate(
        SongMetadata song,
        string? sourceArtist,
        string? sourceTitle,
        DeezerCatalogTrack track,
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

        var fileIsrc = NormalizeIsrc(song.Isrc);
        var trackIsrc = NormalizeIsrc(track.Isrc);
        if (!string.IsNullOrEmpty(fileIsrc))
        {
            if (!string.IsNullOrEmpty(trackIsrc))
            {
                if (string.Equals(fileIsrc, trackIsrc, StringComparison.Ordinal))
                    score = Math.Min(1.0, score + IsrcConfidenceBoost);
                else
                {
                    warnings.Add("isrc_mismatch");
                    score *= IsrcMismatchPenalty;
                }
            }
            else
            {
                warnings.Add("isrc_not_on_deezer_track");
            }
        }

        var songDurationSec = song.DurationSeconds
            ?? (song.DurationMs is int ms ? ms / 1000.0 : (double?)null);
        if (songDurationSec is not null && track.DurationMs > 0)
        {
            var delta = Math.Abs(songDurationSec.Value - track.DurationMs / 1000.0);
            if (delta > opts.DeezerApiDurationDeltaThresholdSeconds)
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
        warnings.Exists(static w => w is "duration_mismatch" or "artist_mismatch" or "title_mismatch" or "isrc_mismatch" or "version_mismatch" or "artist_unknown");

    private static string NormalizeIsrc(string? isrc)
    {
        if (string.IsNullOrWhiteSpace(isrc))
            return string.Empty;
        return isrc.Trim().ToUpperInvariant().Replace("-", "", StringComparison.Ordinal);
    }
}
