using FuzzySharp;
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
        || (!string.IsNullOrWhiteSpace(song.Artist) && !string.IsNullOrWhiteSpace(song.Title));

    public async Task<ProviderOutcome> TryEnrichAsync(SongMetadata song, CancellationToken ct = default)
    {
        var opts = options.Value;

        try
        {
            // 1) Exact lookup by an MBID the file already carries.
            if (!string.IsNullOrWhiteSpace(song.MusicBrainzId))
            {
                var byId = await service.LookupByRecordingIdAsync(song.MusicBrainzId!, ct);
                if (byId is not null)
                    return BuildOutcome(song, byId, opts, exactIdentifier: true);
            }

            // 2) Exact lookup by the file's ISRC tag.
            if (!string.IsNullOrWhiteSpace(song.Isrc))
            {
                var byIsrc = await service.LookupByIsrcAsync(song.Isrc!, ct);
                if (byIsrc is not null)
                    return BuildOutcome(song, byIsrc, opts, exactIdentifier: true);
            }

            // 3) Fall back to a name search.
            if (!string.IsNullOrWhiteSpace(song.Artist) && !string.IsNullOrWhiteSpace(song.Title))
            {
                var results = await service.SearchAsync(song.Artist!, song.Title!, 5, ct);
                if (results.Count == 0)
                    return new ProviderNoMatch();

                MusicBrainzRecording? best = null;
                double bestScore = 0;
                List<string> bestWarnings = [];
                foreach (var candidate in results)
                {
                    var (score, warnings) = Score(song, candidate, opts);
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
        SongMetadata song, MusicBrainzRecording rec, MusicEnricherOptions opts, bool exactIdentifier)
    {
        // An exact-identifier hit is high-confidence, but still verify the returned recording
        // is consistent with the file's existing tags so a stale/wrong tag can't sail through.
        var (score, warnings) = Score(song, rec, opts);
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
            Album: rec.ReleaseTitle);
    }

    private static (double Score, List<string> Warnings) Score(
        SongMetadata song, MusicBrainzRecording rec, MusicEnricherOptions opts)
    {
        var warnings = new List<string>();

        var songArtist = TitleNormalizer.NormalizeForSearch(song.Artist);
        var songTitle = TitleNormalizer.NormalizeForSearch(song.Title);
        var candArtist = TitleNormalizer.NormalizeForSearch(rec.Artist);
        var candTitle = TitleNormalizer.NormalizeForSearch(rec.Title);

        var artistRatio = songArtist.Length == 0 || candArtist.Length == 0 ? 100.0 : Fuzz.WeightedRatio(songArtist, candArtist);
        var titleRatio = songTitle.Length == 0 || candTitle.Length == 0 ? 100.0 : Fuzz.WeightedRatio(songTitle, candTitle);

        if (artistRatio < 85) warnings.Add("artist_mismatch");
        if (titleRatio < 85) warnings.Add("title_mismatch");

        var local = (artistRatio / 100.0 + titleRatio / 100.0) / 2.0;
        // Blend MusicBrainz's own Lucene relevance (40%) with local fuzzy agreement (60%).
        var score = 0.6 * local + 0.4 * (Math.Clamp(rec.Score, 0, 100) / 100.0);

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
        warnings.Exists(static w => w is "artist_mismatch" or "title_mismatch" or "version_mismatch" or "duration_mismatch");
}
