using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment.Providers;

/// <summary>
/// The shared match-decision pipeline for the catalog (name-based) enrichment providers
/// (Deezer, Apple Music, Spotify). Each provider searches its own catalog and scores candidates
/// with its own tuning, but the <b>decision</b> is identical across all three: pick the
/// highest-scoring candidate, then map that score to a Matched / NeedsReview / no-match verdict
/// against a confidence floor and a matched threshold, with a blocking-warning veto.
/// <para>
/// Centralising the loop and the threshold verdict here keeps that rule — including the tolerance
/// epsilon and the "below floor still surfaces as a review hint" behaviour — in one tested place
/// instead of three copies that have to be kept in lock-step by hand.
/// </para>
/// </summary>
public static class CatalogMatchResolver
{
    /// <summary>Small tolerance so a score sitting exactly on a threshold counts as meeting it.</summary>
    private const double ThresholdEpsilon = 1e-9;

    /// <summary>The highest-scoring candidate from a provider's search, with its score and warnings.</summary>
    public sealed record ScoredCandidate<T>(T Candidate, double Score, List<string> Warnings) where T : class;

    /// <summary>
    /// Per-provider confidence knobs. A score below <paramref name="MinConfidence"/> is not even a
    /// review candidate; a score at or above <paramref name="MatchedThreshold"/> (absent a blocking
    /// warning) is an automatic match; anything between is <see cref="EnrichmentStatus.NeedsReview"/>.
    /// </summary>
    public readonly record struct MatchThresholds(double MinConfidence, double MatchedThreshold);

    /// <summary>
    /// Picks the single highest-scoring candidate. Mirrors the providers' original loop exactly: a
    /// strict <c>&gt;</c> comparison seeded at 0, so ties keep the first-seen candidate and an
    /// all-zero field yields no winner (returns <c>null</c>).
    /// </summary>
    public static ScoredCandidate<T>? SelectBest<T>(
        IEnumerable<T> candidates,
        Func<T, (double Score, List<string> Warnings)> score) where T : class
    {
        T? best = null;
        double bestScore = 0;
        List<string> bestWarnings = [];

        foreach (var candidate in candidates)
        {
            var (candidateScore, warnings) = score(candidate);
            if (candidateScore > bestScore)
            {
                bestScore = candidateScore;
                best = candidate;
                bestWarnings = warnings;
            }
        }

        return best is null ? null : new ScoredCandidate<T>(best, bestScore, bestWarnings);
    }

    /// <summary>
    /// Maps a chosen candidate's <paramref name="score"/> and <paramref name="warnings"/> to a
    /// provider outcome:
    /// <list type="bullet">
    /// <item>below <see cref="MatchThresholds.MinConfidence"/> → <see cref="ProviderNoMatch"/>, but the
    /// candidate is still carried as a review hint via <see cref="ProviderNoMatch.BestCandidate"/>;</item>
    /// <item>at/above <see cref="MatchThresholds.MatchedThreshold"/> with no blocking warning →
    /// <see cref="EnrichmentStatus.Matched"/>;</item>
    /// <item>otherwise → <see cref="EnrichmentStatus.NeedsReview"/>.</item>
    /// </list>
    /// <paramref name="buildResult"/> turns the resolved <see cref="EnrichmentStatus"/> into the
    /// provider's own <see cref="EnrichmentProviderResult"/> (each provider maps its catalog track shape).
    /// </summary>
    public static ProviderOutcome Finalize(
        double score,
        IReadOnlyList<string> warnings,
        MatchThresholds thresholds,
        Func<EnrichmentStatus, EnrichmentProviderResult> buildResult)
    {
        if (score < thresholds.MinConfidence - ThresholdEpsilon)
            return new ProviderNoMatch(buildResult(EnrichmentStatus.NeedsReview));

        var blocking = MatchWarnings.AnyBlocking(warnings);
        var status = score >= thresholds.MatchedThreshold - ThresholdEpsilon && !blocking
            ? EnrichmentStatus.Matched
            : EnrichmentStatus.NeedsReview;

        return new ProviderMatched(buildResult(status));
    }
}
