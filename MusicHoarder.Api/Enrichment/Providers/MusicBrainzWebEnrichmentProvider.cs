using Microsoft.Extensions.Options;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment.Providers;

/// <summary>
/// MusicBrainz web-service provider. Uses identifiers the file already carries (MBID/ISRC
/// tags) for exact lookups, otherwise an artist+title search scored against the local tags
/// with version-qualifier awareness. Searches independently of other providers, so when it
/// lands on the same recording as AcoustID it counts as genuine corroboration.
/// </summary>
public class MusicBrainzWebEnrichmentProvider(
    IMusicBrainzWebService service,
    IOptions<MusicEnricherOptions> options,
    ILogger<MusicBrainzWebEnrichmentProvider> logger) : IEnrichmentProvider
{
    public string Name => "MusicBrainzWeb";
    public int Priority => 200;

    public bool CanHandle(SongMetadata song) =>
        !string.IsNullOrWhiteSpace(song.MusicBrainzId)
        || !string.IsNullOrWhiteSpace(song.Isrc)
        || SongSearchText.HasSearchableText(song, options.Value.SourceDirectory);

    public async Task<ProviderOutcome> TryEnrichAsync(SongMetadata song, CancellationToken ct = default)
    {
        var opts = options.Value;
        var resolved = SongSearchText.ResolveDetailed(song, opts.SourceDirectory);
        var (effectiveArtist, effectiveAlbum, effectiveTitle) = (resolved.Artist, resolved.Album, resolved.Title);

        try
        {
            // 1) Exact lookup by an MBID the file already carries (or one a prior provider gossiped).
            if (!string.IsNullOrWhiteSpace(song.MusicBrainzId))
            {
                var byId = await service.LookupByRecordingIdAsync(song.MusicBrainzId!, ct);
                if (byId is not null)
                    return BuildOutcome(song, effectiveArtist, effectiveTitle, effectiveAlbum, byId, opts, exactIdentifier: true);
            }

            // 2) Exact lookup by the file's ISRC tag.
            if (!string.IsNullOrWhiteSpace(song.Isrc))
            {
                var byIsrc = await service.LookupByIsrcAsync(song.Isrc!, ct);
                if (byIsrc is not null)
                    return BuildOutcome(song, effectiveArtist, effectiveTitle, effectiveAlbum, byIsrc, opts, exactIdentifier: true);
            }

            // 3) Fall back to a name search (tags, or path-derived for untagged files).
            if (!string.IsNullOrWhiteSpace(effectiveArtist) && !string.IsNullOrWhiteSpace(effectiveTitle))
            {
                var results = await service.SearchAsync(effectiveArtist!, effectiveTitle!, 5, effectiveAlbum, ct);
                if (results.Count == 0)
                    return new ProviderNoMatch();

                MusicBrainzRecording? best = null;
                double bestScore = 0;
                List<string> bestWarnings = [];
                foreach (var candidate in results)
                {
                    var (score, warnings) = Score(song, effectiveArtist, effectiveTitle, effectiveAlbum, candidate, opts);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = candidate;
                        bestWarnings = warnings;
                    }
                }

                if (best is null)
                    return new ProviderNoMatch();

                return BuildSearchOutcome(song, best, bestScore, bestWarnings, opts);
            }

            return new ProviderNoMatch();
        }
        catch (ProviderRateLimitedException ex)
        {
            logger.LogWarning("MusicBrainz rate limited for song {SongId}, retry after {Delay}s",
                song.Id, ex.RetryAfter.TotalSeconds);
            return new ProviderRateLimited(ex.RetryAfter);
        }
    }

    private static ProviderOutcome BuildOutcome(
        SongMetadata song, string? sourceArtist, string? sourceTitle, string? sourceAlbum,
        MusicBrainzRecording rec, MusicEnricherOptions opts, bool exactIdentifier)
    {
        // An exact-identifier hit is high-confidence, but still verify the returned recording
        // is consistent with the file's existing tags so a stale/wrong tag can't sail through.
        var (score, warnings) = Score(song, sourceArtist, sourceTitle, sourceAlbum, rec, opts);
        var confidence = exactIdentifier ? Math.Max(score, 0.9) : score;

        if (rec.CandidateCount > 1)
        {
            warnings.Add("multiple_isrc_recordings");
            confidence = Math.Min(confidence, opts.MusicBrainzMatchedThreshold - 0.01);
        }

        var status = confidence >= opts.MusicBrainzMatchedThreshold && !HasBlockingWarning(warnings)
            ? EnrichmentStatus.Matched
            : EnrichmentStatus.NeedsReview;

        return new ProviderMatched(BuildResult(song, rec, confidence, warnings, status));
    }

    private static ProviderOutcome BuildSearchOutcome(
        SongMetadata song, MusicBrainzRecording rec, double score, List<string> warnings, MusicEnricherOptions opts)
    {
        if (score < opts.MusicBrainzMinConfidence)
            return new ProviderNoMatch(BuildResult(song, rec, score, warnings, EnrichmentStatus.NeedsReview));

        var status = score >= opts.MusicBrainzMatchedThreshold && !HasBlockingWarning(warnings)
            ? EnrichmentStatus.Matched
            : EnrichmentStatus.NeedsReview;

        return new ProviderMatched(BuildResult(song, rec, score, warnings, status));
    }

    private static EnrichmentProviderResult BuildResult(
        SongMetadata song, MusicBrainzRecording rec, double score, List<string> warnings, EnrichmentStatus status)
    {
        var artist = string.IsNullOrWhiteSpace(rec.Artist) ? song.Artist : rec.Artist;
        return new EnrichmentProviderResult(
            Artist: artist,
            AlbumArtist: string.IsNullOrWhiteSpace(rec.AlbumArtist) ? ArtistCreditNormalizer.GetPrimaryArtist(artist) : rec.AlbumArtist,
            Title: string.IsNullOrWhiteSpace(rec.Title) ? song.Title : rec.Title,
            Year: rec.Year,
            TrackNumber: null,
            MusicBrainzId: rec.Id,
            MusicBrainzReleaseId: rec.ReleaseId,
            SpotifyId: null,
            AcoustIdTrackId: null,
            Isrc: string.IsNullOrWhiteSpace(rec.Isrc) ? null : rec.Isrc,
            MatchedBy: "MusicBrainzWeb",
            MatchConfidence: Math.Clamp(score, 0, 1),
            MatchWarnings: warnings,
            RecommendedStatus: status,
            Album: rec.ReleaseTitle,
            DurationMs: rec.LengthMs is int len && len > 0 ? len : null);
    }

    private static (double Score, List<string> Warnings) Score(
        SongMetadata song, string? sourceArtist, string? sourceTitle, string? sourceAlbum,
        MusicBrainzRecording rec, MusicEnricherOptions opts)
    {
        var warnings = new List<string>();

        // Raw-fallback aware: a symbol-only artist no longer scores as a free 100%.
        var artistRatio = FuzzyTextMatch.Ratio(sourceArtist, rec.Artist);
        var titleRatio = FuzzyTextMatch.Ratio(sourceTitle, rec.Title);

        if (artistRatio is double ar && ar < 85) warnings.Add("artist_mismatch");
        if (titleRatio is double tr && tr < 85) warnings.Add("title_mismatch");

        double local;
        if (artistRatio is double a && titleRatio is double t)
        {
            local = (a / 100.0 + t / 100.0) / 2.0;
        }
        else if (titleRatio is double tOnly)
        {
            // No usable artist signal — keep the candidate as a review-only suggestion.
            local = tOnly / 100.0;
            warnings.Add("artist_unknown");
        }
        else
        {
            local = 0;
        }

        // Blend MusicBrainz's own Lucene relevance (40%) with local fuzzy agreement (60%).
        var score = 0.6 * local + 0.4 * (Math.Clamp(rec.Score, 0, 100) / 100.0);

        // Album is a confirmation signal only (a track can appear on many releases, so absence is
        // never penalized): nudge up when the release title agrees, flag a soft mismatch otherwise.
        if (FuzzyTextMatch.Ratio(sourceAlbum, rec.ReleaseTitle) is double albumRatio)
        {
            if (albumRatio >= 85)
                score = Math.Min(1.0, score + 0.05);
            else
                warnings.Add("album_mismatch");
        }

        var sourceQual = VersionQualifier.Detect(song.Title, song.Album);
        var candQual = VersionQualifier.Detect(rec.Title, rec.ReleaseTitle);
        if (!VersionQualifier.Compare(sourceQual, candQual))
        {
            warnings.Add("version_mismatch");
            score *= 0.6;
        }

        var songDuration = song.DurationSeconds ?? (song.DurationMs is int ms ? ms / 1000 : (int?)null);
        if (songDuration is int sd && rec.LengthMs is int len && len > 0)
        {
            if (Math.Abs(sd - len / 1000.0) > opts.IdentityDurationDeltaSeconds + 12)
            {
                warnings.Add("duration_mismatch");
                score *= 0.7;
            }
        }

        return (Math.Clamp(score, 0, 1), warnings);
    }

    private static bool HasBlockingWarning(List<string> warnings) =>
        warnings.Exists(static w => w is "artist_mismatch" or "title_mismatch" or "version_mismatch" or "duration_mismatch" or "artist_unknown");
}
