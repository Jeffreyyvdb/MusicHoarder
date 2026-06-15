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
            // 1) Exact lookup by an MBID the file itself carries (a prior provider's discovery is
            //    never gossiped onto the row — providers match independently for honest consensus).
            if (!string.IsNullOrWhiteSpace(song.MusicBrainzId))
            {
                var byId = await service.LookupByRecordingIdAsync(song.MusicBrainzId!, ct);
                if (byId is not null)
                    return BuildOutcome(song, resolved, byId, opts, exactIdentifier: true);
            }

            // 2) Exact lookup by the file's ISRC tag.
            if (!string.IsNullOrWhiteSpace(song.Isrc))
            {
                var byIsrc = await service.LookupByIsrcAsync(song.Isrc!, ct);
                if (byIsrc is not null)
                    return BuildOutcome(song, resolved, byIsrc, opts, exactIdentifier: true);
            }

            // 3) Fall back to a name search. MusicBrainz ranks a bare free-text query poorly (it can't
            //    tell artist tokens from title tokens), so for untagged files prefer a FIELDED query
            //    from the "Artist - Title" filename split when we have one; only fall back to free-text
            //    when the filename doesn't cleanly carry the artist.
            var pathQuery = resolved.IdentityFromPath ? resolved.PathQuery : null;
            if (!string.IsNullOrWhiteSpace(effectiveArtist) && !string.IsNullOrWhiteSpace(effectiveTitle))
            {
                IReadOnlyList<MusicBrainzRecording> results;
                if (resolved.IdentityFromPath
                    && resolved.FilenameCarriesArtist
                    && !string.IsNullOrWhiteSpace(resolved.SplitArtist)
                    && !string.IsNullOrWhiteSpace(resolved.SplitTitle))
                {
                    results = await service.SearchAsync(resolved.SplitArtist!, resolved.SplitTitle!, 5, null, ct);
                }
                else if (!string.IsNullOrWhiteSpace(pathQuery))
                {
                    results = await service.SearchFreeTextAsync(pathQuery!, 5, ct);
                }
                else
                {
                    results = await service.SearchAsync(effectiveArtist!, effectiveTitle!, 5, effectiveAlbum, ct);
                }
                if (results.Count == 0)
                    return new ProviderNoMatch();

                MusicBrainzRecording? best = null;
                double bestScore = 0;
                List<string> bestWarnings = [];
                foreach (var candidate in results)
                {
                    var (score, warnings) = Score(song, resolved, candidate, opts);
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
        SongMetadata song, SongSearchText.Resolved source,
        MusicBrainzRecording rec, MusicEnricherOptions opts, bool exactIdentifier)
    {
        // An exact-identifier hit is high-confidence, but still verify the returned recording
        // is consistent with the file's existing tags so a stale/wrong tag can't sail through.
        var (score, warnings) = Score(song, source, rec, opts);
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
            // Prefer the release-level album-artist. Never fall back to the *track* artist credit —
            // on compilations/collabs it's a featured guest and comma-names ("Tyler, The Creator") get
            // truncated, both of which split one album into several. Keep the song's curated album-artist
            // otherwise; only resort to the track's primary artist for genuinely untagged files.
            AlbumArtist: !string.IsNullOrWhiteSpace(rec.AlbumArtist) ? rec.AlbumArtist
                : !string.IsNullOrWhiteSpace(song.AlbumArtist) ? song.AlbumArtist
                : ArtistCreditNormalizer.GetPrimaryArtist(artist),
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
            Artists: rec.Artists,
            ArtistMusicBrainzIds: rec.ArtistMusicBrainzIds,
            AlbumArtistMusicBrainzId: rec.AlbumArtistMusicBrainzId,
            MusicBrainzReleaseGroupId: rec.ReleaseGroupId,
            TotalDiscs: rec.TotalDiscs,
            TotalTracks: rec.TotalTracks,
            IsCompilation: rec.IsCompilation ? true : null,
            ReleaseTypePrimary: rec.ReleaseTypePrimary,
            ReleaseTypes: rec.ReleaseTypes,
            DurationSeconds: rec.LengthMs is int len && len > 0 ? len / 1000 : null);
    }

    private static (double Score, List<string> Warnings) Score(
        SongMetadata song, SongSearchText.Resolved source,
        MusicBrainzRecording rec, MusicEnricherOptions opts)
    {
        var warnings = new List<string>();

        // Embedded tags score (and block) as before; a path-derived identity is corroborated by
        // token-presence against the filename free-text and flagged identity_unverified (non-blocking).
        var local = SourceIdentityScorer.Score(source, rec.Artist, rec.Title, 85, warnings);

        // Blend MusicBrainz's own Lucene relevance (40%) with local fuzzy agreement (60%).
        var score = 0.6 * local + 0.4 * (Math.Clamp(rec.Score, 0, 100) / 100.0);

        // Album is a confirmation signal only (a track can appear on many releases, so absence is
        // never penalized): nudge up when the release title agrees, flag a soft mismatch otherwise.
        // Skipped when the album is itself a path guess (no real signal to confirm against).
        if (!source.AlbumFromPath && FuzzyTextMatch.Ratio(source.Album, rec.ReleaseTitle) is double albumRatio)
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

    private static bool HasBlockingWarning(List<string> warnings) => MatchWarnings.AnyBlocking(warnings);
}
