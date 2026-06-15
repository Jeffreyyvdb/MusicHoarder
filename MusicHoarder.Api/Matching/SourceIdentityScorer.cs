using MusicHoarder.Api.Enrichment;

namespace MusicHoarder.Api.Matching;

/// <summary>
/// Scores a provider candidate's artist+title against the file's expected identity, and appends the
/// appropriate warnings. Shared by the name-based providers (Spotify, MusicBrainz, Deezer, Apple Music)
/// so the embedded-tag-vs-path-guess decision can't drift between them.
/// <para>
/// <b>Embedded tags</b> are treated as the file declaring its own identity: a disagreement emits the
/// blocking <c>artist_mismatch</c>/<c>title_mismatch</c>/<c>artist_unknown</c> warnings (unchanged
/// behaviour). <b>Path-derived</b> artist/title are only a guess — a disagreement must not block. Those
/// candidates are instead corroborated by token-presence against the cleaned filename free-text
/// (<see cref="SongSearchText.Resolved.RawSearchText"/>) and flagged
/// <see cref="MatchWarnings.IdentityUnverified"/> (non-blocking) so the
/// <see cref="Enrichment.ConsensusEvaluator"/> requires a second provider before auto-matching.
/// </para>
/// </summary>
public static class SourceIdentityScorer
{
    /// <summary>
    /// Component score in 0..1 for how well the candidate matches the file's artist+title, plus the
    /// warnings appended along the way. Callers fold this into their provider score and thresholds.
    /// </summary>
    public static double Score(
        in SongSearchText.Resolved source,
        string? candidateArtist,
        string? candidateTitle,
        double fuzzyThreshold,
        List<string> warnings)
    {
        if (source.IdentityFromPath)
            return ScorePathDerived(source, candidateArtist, candidateTitle, warnings);

        return ScoreTagged(source.Artist, source.Title, candidateArtist, candidateTitle, fuzzyThreshold, warnings);
    }

    // Embedded-tag path: identical to the long-standing per-provider logic.
    private static double ScoreTagged(
        string? sourceArtist,
        string? sourceTitle,
        string? candidateArtist,
        string? candidateTitle,
        double fuzzyThreshold,
        List<string> warnings)
    {
        var artistRatio = FuzzyTextMatch.Ratio(sourceArtist, candidateArtist);
        var titleRatio = FuzzyTextMatch.Ratio(sourceTitle, candidateTitle);

        if (artistRatio is double ar && ar < fuzzyThreshold)
            warnings.Add("artist_mismatch");
        if (titleRatio is double tr && tr < fuzzyThreshold)
            warnings.Add("title_mismatch");

        if (artistRatio is double a && titleRatio is double t)
            return (a / 100.0 + t / 100.0) / 2.0;

        if (titleRatio is double tOnly)
        {
            // No usable artist signal — a title-only agreement isn't enough to auto-match.
            warnings.Add("artist_unknown");
            return tOnly / 100.0;
        }

        return 0;
    }

    // Path-derived path: the expected artist/title are guesses, so never block. Corroborate the
    // candidate against the filename free-text by token-presence instead.
    private static double ScorePathDerived(
        in SongSearchText.Resolved source,
        string? candidateArtist,
        string? candidateTitle,
        List<string> warnings)
    {
        warnings.Add(MatchWarnings.IdentityUnverified);

        // Validate against the same free-text the provider queried on (PathQuery = folder-artist guess +
        // cleaned filename), so a structured "<Artist>/<Album>/NN Title" file — whose artist lives in the
        // folder, not the filename — still corroborates the artist. Junk tokens in the haystack (e.g. an
        // "slskd"/bucket folder) only add noise; we check that the candidate's tokens appear, never the
        // reverse.
        var haystack = source.PathQuery;
        if (!string.IsNullOrWhiteSpace(haystack))
        {
            // Title carries the bulk of the signal; the artist corroborates it. Both present → ~1.0;
            // title present but artist absent → 0.5 (a possible cover/different artist → review).
            var titlePresence = CandidateTextMatch.Containment(candidateTitle, haystack);
            var artistPresence = CandidateTextMatch.Containment(candidateArtist, haystack);
            return titlePresence * (0.5 + 0.5 * artistPresence);
        }

        // No free-text to validate against: fall back to fuzzy on the positional guess, still
        // non-blocking (identity_unverified already added; no artist/title_mismatch emitted).
        var ar = FuzzyTextMatch.Ratio(source.Artist, candidateArtist);
        var tr = FuzzyTextMatch.Ratio(source.Title, candidateTitle);
        if (ar is double a && tr is double t)
            return (a / 100.0 + t / 100.0) / 2.0;
        if (tr is double tOnly)
            return tOnly / 100.0 * 0.5;
        return 0;
    }
}
