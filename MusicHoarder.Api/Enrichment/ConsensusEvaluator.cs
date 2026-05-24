using System.Text.Json;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment;

/// <summary>
/// Decides a song's overall <see cref="EnrichmentStatus"/> from the set of provider
/// attempts by <b>agreement</b> rather than trusting any single provider:
/// <list type="bullet">
/// <item>≥2 independent providers landing on the same identity → <c>Matched</c>.</item>
/// <item>A single <i>name-based</i> provider (Spotify / MusicBrainz) that recommended
/// <c>Matched</c> on its own (tuned) thresholds → <c>Matched</c>.</item>
/// <item>A confident community-tracker match (unreleased / leaks the mainstream catalogs lack,
/// so they can never corroborate) that recommended <c>Matched</c> → <c>Matched</c>.</item>
/// <item>A confident custom-rule match (a user-defined pattern rewrote/validated the file's own
/// tags) that recommended <c>Matched</c> → <c>Matched</c>.</item>
/// <item>AcoustID (fingerprint) alone is treated as a candidate only → <c>NeedsReview</c>;
/// it must be corroborated to promote.</item>
/// </list>
/// </summary>
public static class ConsensusEvaluator
{
    /// <summary>Minimum own-confidence for a candidate to act as a corroborating vote.</summary>
    public const double DefaultCorroborationFloor = 0.5;

    public static readonly IdentityMatchOptions DefaultIdentityOptions = IdentityMatchOptions.Default;

    public sealed record ConsensusResult(
        EnrichmentStatus Status,
        EnrichmentProviderResult? Winner,
        double Confidence,
        IReadOnlyList<EnrichmentProvider> AgreeingProviders);

    private sealed record Candidate(
        EnrichmentProvider Provider,
        EnrichmentProviderResult Result,
        ProviderIdentity Identity,
        bool IsNameBased,
        double Confidence);

    public static ConsensusResult Evaluate(
        SongMetadata song,
        IReadOnlySet<EnrichmentProvider> enabledProviders,
        IdentityMatchOptions identityOptions,
        double corroborationFloor = DefaultCorroborationFloor)
    {
        if (enabledProviders.Count == 0)
            return new ConsensusResult(EnrichmentStatus.NeedsReview, null, 0, []);

        var attempts = song.ProviderAttempts
            .Where(a => enabledProviders.Contains(a.Provider))
            .ToList();

        if (attempts.Count == 0)
            return new ConsensusResult(EnrichmentStatus.Pending, null, 0, []);

        // Strong preference: a confident community-tracker match is authoritative for the artists
        // it covers (the tracker only fires for its allowlist, e.g. Juice WRLD). Those catalogs
        // carry leaks / alternate versions / leaked albums the mainstream services lack, so when
        // the tracker confidently matched we prefer it outright — ahead of a mainstream
        // multi-provider consensus, and without waiting on a rate-limited mainstream provider.
        var trackerWinner = TryTrackerPreference(attempts, enabledProviders, corroborationFloor);
        if (trackerWinner is not null)
            return trackerWinner;

        // A rate-limited provider means the picture is incomplete — stay Pending.
        if (attempts.Any(a => a.Status == ProviderAttemptStatus.RateLimited))
            return new ConsensusResult(EnrichmentStatus.Pending, null, 0, []);

        var candidates = new List<Candidate>();
        foreach (var attempt in attempts)
        {
            if (attempt.Status != ProviderAttemptStatus.Matched || attempt.MatchedDataJson is null)
                continue;

            var result = TryDeserialize(attempt.MatchedDataJson);
            if (result is null)
                continue;

            candidates.Add(new Candidate(
                attempt.Provider,
                result,
                ToIdentity(result),
                IsNameBased(attempt.Provider),
                result.MatchConfidence));
        }

        var allAttempted = enabledProviders.All(p =>
            attempts.Any(a => a.Provider == p &&
                a.Status is ProviderAttemptStatus.Matched
                    or ProviderAttemptStatus.NoMatch
                    or ProviderAttemptStatus.Failed));

        if (candidates.Count == 0)
        {
            if (!allAttempted)
                return new ConsensusResult(EnrichmentStatus.Pending, null, 0, []);
            var anyFailed = attempts.Any(a => a.Status == ProviderAttemptStatus.Failed);
            return new ConsensusResult(
                anyFailed ? EnrichmentStatus.Failed : EnrichmentStatus.NeedsReview, null, 0, []);
        }

        // 1) Multi-provider corroboration: cluster the candidates strong enough to vote.
        var strong = candidates.Where(c => c.Confidence >= corroborationFloor).ToList();
        var bestCluster = BuildBestCluster(strong, identityOptions);
        if (bestCluster is not null)
        {
            var distinctProviders = bestCluster.Select(c => c.Provider).Distinct().ToList();
            if (distinctProviders.Count >= 2)
            {
                var winner = bestCluster
                    .OrderByDescending(c => c.IsNameBased)
                    .ThenByDescending(c => c.Confidence)
                    .First();
                return new ConsensusResult(
                    EnrichmentStatus.Matched, winner.Result, CombineConfidence(bestCluster), distinctProviders);
            }
        }

        // 2) A name-based provider that matched on its own tuned thresholds may stand alone.
        var soloNameBased = strong
            .Where(c => c.IsNameBased && c.Result.RecommendedStatus == EnrichmentStatus.Matched)
            .OrderByDescending(c => c.Confidence)
            .FirstOrDefault();
        if (soloNameBased is not null)
        {
            return new ConsensusResult(
                EnrichmentStatus.Matched, soloNameBased.Result, soloNameBased.Confidence,
                [soloNameBased.Provider]);
        }

        // (A confident community-tracker match is handled with top precedence above.)

        // 3) A clean, *unambiguous* fingerprint match (AcoustID) is acoustically authoritative and
        //    may stand alone — but only once every enabled provider has had its turn, so a
        //    name-based corroborator/contradictor gets the chance to weigh in first. The validator
        //    already recommended Matched (score >= threshold, no blocking warning), and we further
        //    require a single candidate so an ambiguous fingerprint that resolved to several
        //    recordings still goes to review.
        if (allAttempted)
        {
            var soloFingerprint = strong
                .Where(c => !c.IsNameBased
                    && c.Result.RecommendedStatus == EnrichmentStatus.Matched
                    && !c.Result.MatchWarnings.Contains("multiple_candidates"))
                .OrderByDescending(c => c.Confidence)
                .FirstOrDefault();

            // Don't stand alone if a strong name-based provider landed on a *different* identity
            // (e.g. a different version/recording) — that genuine conflict belongs in review.
            var contradictedByNameBased = soloFingerprint is not null && strong.Any(c =>
                c.IsNameBased && !c.Identity.AgreesWith(soloFingerprint.Identity, identityOptions));

            if (soloFingerprint is not null && !contradictedByNameBased)
            {
                return new ConsensusResult(
                    EnrichmentStatus.Matched, soloFingerprint.Result, soloFingerprint.Confidence,
                    [soloFingerprint.Provider]);
            }
        }

        // 4) Otherwise it needs review (once every provider has had its turn). Surface the
        //    highest-confidence candidate so the review UI / bulk-approve can find it.
        var reviewWinner = candidates.OrderByDescending(c => c.Confidence).First();
        if (!allAttempted)
            return new ConsensusResult(EnrichmentStatus.Pending, null, 0, []);

        return new ConsensusResult(
            EnrichmentStatus.NeedsReview, reviewWinner.Result, reviewWinner.Confidence,
            [reviewWinner.Provider]);
    }

    /// <summary>
    /// Returns a winning <see cref="ConsensusResult"/> when the community tracker confidently
    /// matched (recommended <see cref="EnrichmentStatus.Matched"/> at or above the corroboration
    /// floor), or null when it didn't. The tracker only produces a candidate for the artists it
    /// covers, so this is the "prefer the tracker for those songs" rule.
    /// </summary>
    private static ConsensusResult? TryTrackerPreference(
        List<SongProviderAttempt> attempts,
        IReadOnlySet<EnrichmentProvider> enabledProviders,
        double corroborationFloor)
    {
        // Community trackers are single-artist catalogs gated by disjoint allowlists, so at most
        // one of them has a matched attempt for any given song. Prefer whichever fired.
        foreach (var trackerProvider in TrackerProviders)
        {
            if (!enabledProviders.Contains(trackerProvider))
                continue;

            var attempt = attempts.FirstOrDefault(a =>
                a.Provider == trackerProvider
                && a.Status == ProviderAttemptStatus.Matched
                && a.MatchedDataJson is not null);
            if (attempt is null)
                continue;

            var result = TryDeserialize(attempt.MatchedDataJson!);
            if (result is null
                || result.RecommendedStatus != EnrichmentStatus.Matched
                || result.MatchConfidence < corroborationFloor)
                continue;

            return new ConsensusResult(
                EnrichmentStatus.Matched, result, result.MatchConfidence, [trackerProvider]);
        }

        return null;
    }

    private static List<Candidate>? BuildBestCluster(List<Candidate> strong, IdentityMatchOptions opts)
    {
        if (strong.Count == 0)
            return null;

        var clusters = new List<List<Candidate>>();
        foreach (var candidate in strong)
        {
            var placed = false;
            foreach (var cluster in clusters)
            {
                if (cluster.Any(m => m.Identity.AgreesWith(candidate.Identity, opts)))
                {
                    cluster.Add(candidate);
                    placed = true;
                    break;
                }
            }

            if (!placed)
                clusters.Add([candidate]);
        }

        return clusters
            .OrderByDescending(c => c.Select(x => x.Provider).Distinct().Count())
            .ThenByDescending(c => c.Sum(x => x.Confidence))
            .First();
    }

    private static double CombineConfidence(IEnumerable<Candidate> cluster)
    {
        var inverse = 1.0;
        foreach (var c in cluster)
            inverse *= 1.0 - Math.Clamp(c.Confidence, 0, 1);
        return Math.Min(0.99, 1.0 - inverse);
    }

    /// <summary>
    /// Trusted single-source providers, in preference order, that win outright on a confident match.
    /// Community trackers (leaks the mainstream catalogs lack) and the custom-rule provider (user
    /// patterns are authoritative for the files they match, which the catalogs can't corroborate).
    /// </summary>
    private static readonly EnrichmentProvider[] TrackerProviders =
        [EnrichmentProvider.Tracker, EnrichmentProvider.YeTracker, EnrichmentProvider.CustomRule];

    public static bool IsNameBased(EnrichmentProvider provider)
        => provider is EnrichmentProvider.SpotifyAPI
            or EnrichmentProvider.MusicBrainzWeb
            or EnrichmentProvider.Deezer
            or EnrichmentProvider.AppleMusic;

    private static ProviderIdentity ToIdentity(EnrichmentProviderResult r)
        => new(
            r.Artist,
            r.Title,
            r.Album,
            DurationSeconds: null,
            r.Isrc,
            r.MusicBrainzId,
            r.SpotifyId,
            VersionQualifier.Detect(r.Title, r.Album));

    private static EnrichmentProviderResult? TryDeserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<EnrichmentProviderResult>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
