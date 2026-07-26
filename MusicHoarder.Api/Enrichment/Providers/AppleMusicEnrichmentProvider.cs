using Microsoft.Extensions.Options;
using MusicHoarder.Api.AppleMusic;
using MusicHoarder.Api.Matching;
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
        if (string.IsNullOrWhiteSpace(resolved.Artist) || string.IsNullOrWhiteSpace(resolved.Title))
        {
            logger.LogDebug("Apple Music enrichment: no searchable artist/title (SongId={SongId})", song.Id);
            return new ProviderNoMatch();
        }

        IReadOnlyList<AppleMusicCatalogTrack> tracks;
        try
        {
            tracks = await CatalogSearchPlanner.SearchAsync(
                resolved, (query, token) => catalog.SearchTracksAsync(query, token), ct);
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
        var best = CatalogMatchResolver.SelectBest(
            tracks, track => ScoreCandidate(song, resolved, track, opts));
        if (best is null)
            return new ProviderNoMatch();

        return CatalogMatchResolver.Finalize(
            best.Score,
            best.Warnings,
            new CatalogMatchResolver.MatchThresholds(opts.AppleMusicApiMinConfidence, opts.AppleMusicApiMatchedThreshold),
            status => BuildResult(song, best.Candidate, best.Score, best.Warnings, status));
    }

    private EnrichmentProviderResult BuildResult(
        SongMetadata song,
        AppleMusicCatalogTrack track,
        double score,
        List<string> warnings,
        EnrichmentStatus status)
    {
        var (effectiveArtist, albumArtist) = CatalogResultArtists.Resolve(song, track.Artist);

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
