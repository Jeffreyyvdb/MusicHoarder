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
    public string Name => "AppleMusic";
    public int Priority => 350;

    public bool CanHandle(SongMetadata song) =>
        SongSearchText.HasSearchableText(song, options.Value.SourceDirectory);

    public async Task<ProviderOutcome> TryEnrichAsync(SongMetadata song, CancellationToken ct = default)
    {
        var resolved = SongSearchText.ResolveDetailed(song, options.Value.SourceDirectory);
        var (effectiveArtist, effectiveAlbum, effectiveTitle) = (resolved.Artist, resolved.Album, resolved.Title);
        if (string.IsNullOrWhiteSpace(effectiveArtist) || string.IsNullOrWhiteSpace(effectiveTitle))
        {
            logger.LogDebug("Apple Music enrichment: no searchable artist/title (SongId={SongId})", song.Id);
            return new ProviderNoMatch();
        }

        IReadOnlyList<AppleMusicCatalogTrack> tracks;
        try
        {
            // Untagged files query on the cleaned filename free-text; tagged files keep the discrete
            // artist+title (+album) search. Album sharpens the search so the original pressing surfaces
            // ahead of a compilation; fall back to artist+title so recall never drops.
            var pathQuery = resolved.IdentityFromPath ? resolved.PathQuery : null;
            var baseQuery = string.IsNullOrWhiteSpace(pathQuery)
                ? $"{effectiveArtist} {effectiveTitle}".Trim()
                : pathQuery!.Trim();
            tracks = string.IsNullOrWhiteSpace(effectiveAlbum) || !string.IsNullOrWhiteSpace(pathQuery)
                ? await catalog.SearchTracksAsync(baseQuery, ct)
                : await catalog.SearchTracksAsync($"{baseQuery} {effectiveAlbum}".Trim(), ct);
            if (tracks.Count == 0 && string.IsNullOrWhiteSpace(pathQuery) && !string.IsNullOrWhiteSpace(effectiveAlbum))
                tracks = await catalog.SearchTracksAsync(baseQuery, ct);
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
            var (score, warnings) = ScoreCandidate(song, resolved, track, opts);
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

        var blocking = MatchWarnings.AnyBlocking(bestWarnings);
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
            Isrc: null,
            MatchedBy: Name,
            MatchConfidence: Math.Clamp(score, 0, 1),
            MatchWarnings: warnings,
            RecommendedStatus: status,
            Album: string.IsNullOrWhiteSpace(track.AlbumName) ? null : track.AlbumName,
            DurationSeconds: track.DurationMs > 0 ? track.DurationMs / 1000 : null);
    }

    // iTunes carries no ISRC, so Apple Music omits the ISRC step (Isrc tuning left null) and otherwise
    // mirrors the shared scoring pipeline with the same penalties as Spotify/Deezer.
    private static (double Score, List<string> Warnings) ScoreCandidate(
        SongMetadata song,
        SongSearchText.Resolved source,
        AppleMusicCatalogTrack track,
        MusicEnricherOptions opts)
        => CatalogCandidateScorer.Score(
            song,
            source,
            new CatalogCandidateScorer.CatalogCandidate(
                track.Artist, track.Title, track.AlbumName, Isrc: null, track.DurationMs, track.TrackNumber),
            new CatalogCandidateScorer.ScoringTuning(
                DurationDeltaThresholdSeconds: opts.AppleMusicApiDurationDeltaThresholdSeconds,
                DurationMismatchPenalty: 0.7,
                VersionMismatchPenalty: 0.6,
                AlbumAgreementConfidenceBoost: opts.AlbumAgreementConfidenceBoost,
                Isrc: null));
}
