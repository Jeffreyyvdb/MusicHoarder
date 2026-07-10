using Microsoft.Extensions.Options;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment.Providers;

/// <summary>
/// Free, no-auth Deezer name-based enrichment provider. ISRC-first (using the file's own ISRC tag,
/// if any) then fuzzy artist+title search. The chosen candidate is hydrated for its ISRC so that
/// ISRC participates in genuine cross-provider consensus (two providers independently landing on the
/// same ISRC), not so it can be gossiped onto the row — identifier gossip was removed.
/// </summary>
public class DeezerEnrichmentProvider(
    IDeezerCatalogService catalog,
    IOptions<MusicEnricherOptions> options,
    ILogger<DeezerEnrichmentProvider> logger) : IEnrichmentProvider
{
    public string Name => "Deezer";
    public int Priority => 250;

    public bool CanHandle(SongMetadata song) =>
        SongSearchText.HasSearchableText(song, options.Value.SourceDirectory);

    public async Task<ProviderOutcome> TryEnrichAsync(SongMetadata song, CancellationToken ct = default)
    {
        var resolved = SongSearchText.ResolveDetailed(song, options.Value.SourceDirectory);
        var (effectiveArtist, effectiveAlbum, effectiveTitle) = (resolved.Artist, resolved.Album, resolved.Title);
        if (string.IsNullOrWhiteSpace(effectiveArtist) || string.IsNullOrWhiteSpace(effectiveTitle))
        {
            logger.LogDebug("Deezer enrichment: no searchable artist/title (SongId={SongId})", song.Id);
            return new ProviderNoMatch();
        }

        var opts = options.Value;
        DeezerCatalogTrack bestTrack;

        try
        {
            // Identifier-first: if the file already carries an ISRC, ask Deezer for that exact
            // recording (full detail) before falling back to a fuzzy artist+title search.
            var fileIsrc = ProviderIdentity.NormalizeIsrc(song.Isrc);
            if (!string.IsNullOrEmpty(fileIsrc))
            {
                var isrcTrack = await catalog.LookupByIsrcAsync(fileIsrc, ct);
                if (isrcTrack is not null)
                {
                    logger.LogDebug("Deezer ISRC lookup hit for SongId={SongId} ({Isrc})", song.Id, fileIsrc);
                    var (isrcScore, isrcWarnings) = ScoreCandidate(song, resolved, isrcTrack, opts);
                    return Finalize(song, isrcTrack, isrcScore, isrcWarnings, opts);
                }
            }

            // Untagged files query on the cleaned filename free-text; tagged files keep the discrete
            // artist+title (+album) search. Album sharpens the search so the original pressing surfaces
            // ahead of a compilation; fall back to artist+title so recall never drops.
            var pathQuery = resolved.IdentityFromPath ? resolved.PathQuery : null;
            var baseQuery = string.IsNullOrWhiteSpace(pathQuery)
                ? $"{effectiveArtist} {effectiveTitle}".Trim()
                : pathQuery!.Trim();
            var tracks = string.IsNullOrWhiteSpace(effectiveAlbum) || !string.IsNullOrWhiteSpace(pathQuery)
                ? await catalog.SearchTracksAsync(baseQuery, ct)
                : await catalog.SearchTracksAsync($"{baseQuery} {effectiveAlbum}".Trim(), ct);
            if (tracks.Count == 0 && string.IsNullOrWhiteSpace(pathQuery) && !string.IsNullOrWhiteSpace(effectiveAlbum))
                tracks = await catalog.SearchTracksAsync(baseQuery, ct);

            if (tracks.Count == 0)
            {
                logger.LogDebug("Deezer search returned no tracks for SongId={SongId}", song.Id);
                return new ProviderNoMatch();
            }

            var best = CatalogMatchResolver.SelectBest(
                tracks, track => ScoreCandidate(song, resolved, track, opts));
            if (best is null)
                return new ProviderNoMatch();

            bestTrack = best.Candidate;

            // Hydrate only the chosen candidate to get its ISRC + release year / track #, which
            // Deezer omits from search results (the ISRC feeds independent cross-provider consensus).
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
        var (finalScore, finalWarnings) = ScoreCandidate(song, resolved, bestTrack, opts);
        return Finalize(song, bestTrack, finalScore, finalWarnings, opts);
    }

    private ProviderOutcome Finalize(
        SongMetadata song, DeezerCatalogTrack track, double score, List<string> warnings, MusicEnricherOptions opts)
        => CatalogMatchResolver.Finalize(
            score,
            warnings,
            new CatalogMatchResolver.MatchThresholds(opts.DeezerApiMinConfidence, opts.DeezerApiMatchedThreshold),
            status => BuildResult(song, track, score, warnings, status));

    private EnrichmentProviderResult BuildResult(
        SongMetadata song,
        DeezerCatalogTrack track,
        double score,
        List<string> warnings,
        EnrichmentStatus status)
    {
        var effectiveArtist = string.IsNullOrWhiteSpace(track.Artist) ? song.Artist : track.Artist;
        // Album-artist is an album-level field: never synthesize it from the *track* artist credit,
        // which on compilations/collabs is a featured guest and for comma-names ("Tyler, The Creator")
        // gets truncated by GetPrimaryArtist — both split one album into several. Preserve the song's
        // curated album-artist; only fall back to the track's primary artist for genuinely untagged files.
        var albumArtist = !string.IsNullOrWhiteSpace(song.AlbumArtist)
            ? song.AlbumArtist
            : ArtistCreditNormalizer.GetPrimaryArtist(effectiveArtist) ?? effectiveArtist;

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
            Isrc: string.IsNullOrWhiteSpace(track.Isrc) ? null : ProviderIdentity.NormalizeIsrc(track.Isrc),
            MatchedBy: Name,
            MatchConfidence: Math.Clamp(score, 0, 1),
            MatchWarnings: warnings,
            RecommendedStatus: status,
            Album: string.IsNullOrWhiteSpace(track.AlbumName) ? null : track.AlbumName,
            Artists: track.Artists,
            DurationSeconds: track.DurationMs > 0 ? track.DurationMs / 1000 : null);
    }

    // Deezer's tuned values mirror Spotify's; only the threshold knobs differ. The shared scorer
    // applies them in the common order (identity → ISRC → duration → version → album).
    private static (double Score, List<string> Warnings) ScoreCandidate(
        SongMetadata song,
        SongSearchText.Resolved source,
        DeezerCatalogTrack track,
        MusicEnricherOptions opts)
        => CatalogCandidateScorer.Score(
            song,
            source,
            new CatalogCandidateScorer.CatalogCandidate(
                track.Artist, track.Title, track.AlbumName, track.Isrc, track.DurationMs, track.TrackNumber),
            new CatalogCandidateScorer.ScoringTuning(
                DurationDeltaThresholdSeconds: opts.DeezerApiDurationDeltaThresholdSeconds,
                DurationMismatchPenalty: 0.7,
                VersionMismatchPenalty: 0.6,
                AlbumAgreementConfidenceBoost: opts.AlbumAgreementConfidenceBoost,
                Isrc: new CatalogCandidateScorer.IsrcScoring(
                    ConfidenceBoost: 0.12, MismatchPenalty: 0.65, NotOnCandidateWarning: "isrc_not_on_deezer_track")));
}
