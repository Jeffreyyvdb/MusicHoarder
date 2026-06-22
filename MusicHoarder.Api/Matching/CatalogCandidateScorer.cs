using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Matching;

/// <summary>
/// Shared confidence scorer for the name-based catalog providers (Spotify, Deezer, Apple Music).
/// They all answer the same question — "how well does this catalog hit match the file?" — by folding
/// the same signals in the same order:
/// <list type="number">
/// <item>artist + title agreement (delegated to <see cref="SourceIdentityScorer"/>, which encodes the
/// embedded-tag-vs-path-guess rule);</item>
/// <item>an optional ISRC corroboration (boost on an exact match, penalty on a conflict);</item>
/// <item>a duration sanity check;</item>
/// <item>a version-qualifier guard ("Live"/"Remix"/"Acoustic" can't satisfy a studio request);</item>
/// <item>an album-agreement tie-breaker (reward agreement, never penalize a different release);</item>
/// <item>an optional track-number tie-breaker.</item>
/// </list>
/// Only the tuned <i>numbers</i> and which provider carries an ISRC differ between providers; those live
/// in <see cref="ScoringTuning"/>. Keeping the algorithm here means a change to, say, how a duration
/// mismatch is handled is a one-file edit instead of three providers drifting out of sync (which the
/// "mirror SpotifyApiEnrichmentProvider's tuned values" comments used to warn about).
/// </summary>
public static class CatalogCandidateScorer
{
    /// <summary>Fuzzy artist/title similarity (0..100) at or above which the signals are treated as agreeing.</summary>
    public const double FuzzyThreshold = 85.0;

    /// <summary>A catalog hit reduced to the fields the scorer needs, mapped from each provider's track type.</summary>
    public readonly record struct CatalogCandidate(
        string? Artist,
        string? Title,
        string? Album,
        string? Isrc,
        int DurationMs,
        int? TrackNumber = null);

    /// <summary>ISRC scoring knobs for a provider whose catalog carries ISRCs (null for one that doesn't, e.g. iTunes).</summary>
    public sealed record IsrcScoring(
        double ConfidenceBoost,
        double MismatchPenalty,
        string NotOnCandidateWarning);

    /// <summary>The per-provider tuned values folded into the otherwise-identical scoring pipeline.</summary>
    public sealed record ScoringTuning(
        double DurationDeltaThresholdSeconds,
        double DurationMismatchPenalty,
        double VersionMismatchPenalty,
        double AlbumAgreementConfidenceBoost,
        IsrcScoring? Isrc = null,
        double TrackNumberBoost = 0.0);

    /// <summary>
    /// Component score in 0..1 for how well <paramref name="candidate"/> matches the file, plus the
    /// warnings accumulated along the way. The score is left un-capped at the upper bound here (the
    /// album/track-number boosts can push it past 1.0 so they still break a tie when artist+title
    /// already saturate); callers clamp the final confidence when they build the provider result.
    /// </summary>
    public static (double Score, List<string> Warnings) Score(
        SongMetadata song,
        SongSearchText.Resolved source,
        CatalogCandidate candidate,
        ScoringTuning tuning)
    {
        var warnings = new List<string>();

        // Embedded tags score (and block) as before; a path-derived identity is corroborated by
        // token-presence against the filename free-text and flagged identity_unverified (non-blocking).
        var score = SourceIdentityScorer.Score(source, candidate.Artist, candidate.Title, FuzzyThreshold, warnings);

        var isrcConfirmed = false;
        if (tuning.Isrc is { } isrcScoring)
            (score, isrcConfirmed) = ScoreIsrc(score, warnings, song.Isrc, candidate.Isrc, isrcScoring);

        score = ScoreDuration(score, warnings, song, candidate.DurationMs, tuning, isrcConfirmed);
        score = ScoreVersion(score, warnings, song, candidate, tuning);
        score = ScoreAlbumAgreement(score, warnings, song.Album, candidate.Album, tuning);

        if (tuning.TrackNumberBoost > 0 && source.TrackNumber is int sourceTrack && candidate.TrackNumber == sourceTrack)
            score += tuning.TrackNumberBoost;

        return (Math.Max(0.0, score), warnings);
    }

    /// <summary>
    /// Folds the ISRC signal into the score and reports whether the file's own ISRC was
    /// <b>confirmed</b> — i.e. present on the file and exactly equal (after normalization) to the
    /// candidate's. A confirmation is decisive identity proof the duration step then trusts.
    /// </summary>
    private static (double Score, bool IsrcConfirmed) ScoreIsrc(
        double score, List<string> warnings, string? songIsrc, string? candidateIsrc, IsrcScoring isrc)
    {
        var fileIsrc = ProviderIdentity.NormalizeIsrc(songIsrc);
        if (string.IsNullOrEmpty(fileIsrc))
            return (score, false);

        var candIsrc = ProviderIdentity.NormalizeIsrc(candidateIsrc);
        if (string.IsNullOrEmpty(candIsrc))
        {
            warnings.Add(isrc.NotOnCandidateWarning);
            return (score, false);
        }

        if (string.Equals(fileIsrc, candIsrc, StringComparison.Ordinal))
            return (Math.Min(1.0, score + isrc.ConfidenceBoost), true);

        warnings.Add("isrc_mismatch");
        return (score * isrc.MismatchPenalty, false);
    }

    private static double ScoreDuration(
        double score, List<string> warnings, SongMetadata song, int candidateDurationMs, ScoringTuning tuning,
        bool isrcConfirmed)
    {
        var songDurationSec = song.DurationSeconds
            ?? (song.DurationMs is int ms ? ms / 1000.0 : (double?)null);
        if (songDurationSec is null || candidateDurationMs <= 0)
            return score;

        var delta = Math.Abs(songDurationSec.Value - candidateDurationMs / 1000.0);
        if (delta <= tuning.DurationDeltaThresholdSeconds)
            return score;

        // An exact ISRC match proves this is the same registered recording, so a length delta is an
        // alternate master/encode (a longer YouTube rip, an outro) — not a wrong match. Record it for
        // visibility but neither block nor penalize. Any other disagreement (artist/title) still blocks.
        if (isrcConfirmed)
        {
            warnings.Add(MatchWarnings.DurationMismatchIsrcConfirmed);
            return score;
        }

        warnings.Add(MatchWarnings.DurationMismatch);
        return score * tuning.DurationMismatchPenalty;
    }

    private static double ScoreVersion(
        double score, List<string> warnings, SongMetadata song, CatalogCandidate candidate, ScoringTuning tuning)
    {
        // Keep a "Live"/"Remix"/"Acoustic" candidate from satisfying a studio request (and vice-versa).
        var sourceQual = VersionQualifier.Detect(song.Title, song.Album);
        var candQual = VersionQualifier.Detect(candidate.Title, candidate.Album);
        if (VersionQualifier.Compare(sourceQual, candQual))
            return score;

        warnings.Add("version_mismatch");
        return score * tuning.VersionMismatchPenalty;
    }

    private static double ScoreAlbumAgreement(
        double score, List<string> warnings, string? songAlbum, string? candidateAlbum, ScoringTuning tuning)
    {
        // Album is a confirmation signal only: a track legitimately appears on many releases, so we
        // reward agreement with the file's album (the original pressing) but never penalize a
        // difference. The boost is un-capped here (the final confidence is clamped by the caller) so it
        // still breaks a tie when artist+title already saturate at 1.0 — exactly the original-album-vs-
        // "Greatest Hits"-reissue case where both otherwise score identically. A path-derived album hint
        // leaves the file's own album null, so FuzzyTextMatch.Ratio returns no signal and we skip it.
        if (FuzzyTextMatch.Ratio(songAlbum, candidateAlbum) is not double albumRatio)
            return score;

        if (albumRatio >= FuzzyThreshold)
            return score + tuning.AlbumAgreementConfidenceBoost;

        warnings.Add("album_mismatch");
        return score;
    }
}
