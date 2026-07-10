using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Enrichment.Providers;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

/// <summary>
/// Unit tests for the shared catalog-provider match pipeline extracted from the Deezer / Apple Music
/// / Spotify enrichment providers. Before the extraction this verdict logic (best-candidate pick +
/// confidence-floor / matched-threshold / blocking-warning mapping) could only be exercised through a
/// full provider with a stubbed catalog; now it stands on its own.
/// </summary>
public class CatalogMatchResolverTests
{
    private sealed record Candidate(string Name);

    private static EnrichmentProviderResult ResultWith(EnrichmentStatus status, double score, List<string> warnings) =>
        new(
            Artist: "A",
            AlbumArtist: "A",
            Title: "T",
            Year: null,
            TrackNumber: null,
            MusicBrainzId: null,
            MusicBrainzReleaseId: null,
            SpotifyId: null,
            AcoustIdTrackId: null,
            Isrc: null,
            MatchedBy: "Test",
            MatchConfidence: score,
            MatchWarnings: warnings,
            RecommendedStatus: status);

    // ── SelectBest ─────────────────────────────────────────────────────────

    [Fact]
    public void SelectBest_PicksHighestScoringCandidate()
    {
        var candidates = new[] { new Candidate("low"), new Candidate("high"), new Candidate("mid") };

        var best = CatalogMatchResolver.SelectBest(
            candidates,
            c => c.Name switch
            {
                "high" => (0.9, new List<string> { "hi" }),
                "mid" => (0.5, new List<string>()),
                _ => (0.1, new List<string>()),
            });

        Assert.NotNull(best);
        Assert.Equal("high", best!.Candidate.Name);
        Assert.Equal(0.9, best.Score);
        Assert.Equal(new[] { "hi" }, best.Warnings);
    }

    [Fact]
    public void SelectBest_TieKeepsFirstSeen()
    {
        var candidates = new[] { new Candidate("first"), new Candidate("second") };

        var best = CatalogMatchResolver.SelectBest(candidates, _ => (0.7, new List<string>()));

        Assert.NotNull(best);
        Assert.Equal("first", best!.Candidate.Name);
    }

    [Fact]
    public void SelectBest_AllZeroScores_ReturnsNull()
    {
        var candidates = new[] { new Candidate("a"), new Candidate("b") };

        var best = CatalogMatchResolver.SelectBest(candidates, _ => (0.0, new List<string>()));

        Assert.Null(best);
    }

    [Fact]
    public void SelectBest_EmptyCandidates_ReturnsNull()
    {
        var best = CatalogMatchResolver.SelectBest(
            Array.Empty<Candidate>(), _ => (1.0, new List<string>()));

        Assert.Null(best);
    }

    // ── Finalize ───────────────────────────────────────────────────────────

    private static readonly CatalogMatchResolver.MatchThresholds Thresholds =
        new(MinConfidence: 0.4, MatchedThreshold: 0.8);

    [Fact]
    public void Finalize_BelowMinConfidence_IsNoMatch_ButCarriesNeedsReviewCandidate()
    {
        var warnings = new List<string>();

        var outcome = CatalogMatchResolver.Finalize(
            score: 0.3, warnings, Thresholds,
            status => ResultWith(status, 0.3, warnings));

        var noMatch = Assert.IsType<ProviderNoMatch>(outcome);
        Assert.NotNull(noMatch.BestCandidate);
        Assert.Equal(EnrichmentStatus.NeedsReview, noMatch.BestCandidate!.RecommendedStatus);
    }

    [Fact]
    public void Finalize_BetweenFloorAndThreshold_IsMatchedOutcome_NeedsReviewStatus()
    {
        var warnings = new List<string>();

        var outcome = CatalogMatchResolver.Finalize(
            score: 0.6, warnings, Thresholds,
            status => ResultWith(status, 0.6, warnings));

        var matched = Assert.IsType<ProviderMatched>(outcome);
        Assert.Equal(EnrichmentStatus.NeedsReview, matched.Result.RecommendedStatus);
    }

    [Fact]
    public void Finalize_AtOrAboveThreshold_NoBlockingWarning_IsMatched()
    {
        var warnings = new List<string>();

        var outcome = CatalogMatchResolver.Finalize(
            score: 0.85, warnings, Thresholds,
            status => ResultWith(status, 0.85, warnings));

        var matched = Assert.IsType<ProviderMatched>(outcome);
        Assert.Equal(EnrichmentStatus.Matched, matched.Result.RecommendedStatus);
    }

    [Fact]
    public void Finalize_ScoreExactlyOnThreshold_CountsAsMatched()
    {
        var warnings = new List<string>();

        var outcome = CatalogMatchResolver.Finalize(
            score: 0.8, warnings, Thresholds,
            status => ResultWith(status, 0.8, warnings));

        var matched = Assert.IsType<ProviderMatched>(outcome);
        Assert.Equal(EnrichmentStatus.Matched, matched.Result.RecommendedStatus);
    }

    [Fact]
    public void Finalize_AboveThreshold_ButBlockingWarning_StaysNeedsReview()
    {
        var warnings = new List<string> { "duration_mismatch" };

        var outcome = CatalogMatchResolver.Finalize(
            score: 0.95, warnings, Thresholds,
            status => ResultWith(status, 0.95, warnings));

        var matched = Assert.IsType<ProviderMatched>(outcome);
        Assert.Equal(EnrichmentStatus.NeedsReview, matched.Result.RecommendedStatus);
    }
}
