using System.Text.Json;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment;

public record MatchValidationResult(
    float AdjustedScore,
    List<string> Warnings,
    EnrichmentStatus RecommendedStatus)
{
    public string? WarningsJson => Warnings.Count > 0
        ? JsonSerializer.Serialize(Warnings)
        : null;
}

public interface IAcoustIdMatchValidator
{
    MatchValidationResult Validate(AcoustIdMatch match, SongMetadata track);
}

public class AcoustIdMatchValidator : IAcoustIdMatchValidator
{
    private const float LowScoreThreshold = 0.7f;
    private const float MatchedThreshold = 0.85f;
    private const float DiscardThreshold = 0.5f;
    private const float ArtistMismatchPenalty = 0.5f;
    private const float DurationMismatchPenalty = 0.7f;
    private const float TitleMismatchPenalty = 0.8f;
    private const double DurationDeltaThresholdSeconds = 20.0;

    public MatchValidationResult Validate(AcoustIdMatch match, SongMetadata track)
    {
        var warnings = new List<string>();
        var score = match.Score;

        if (match.Score < LowScoreThreshold)
            warnings.Add("low_acoustid_score");

        if (match.CandidateCount > 1)
            warnings.Add("multiple_candidates");

        score = CrossValidateArtist(match, track, score, warnings);
        score = CrossValidateDuration(match, track, score, warnings);
        score = CrossValidateTitle(match, track, score, warnings);

        var status = DetermineStatus(score, warnings);
        return new MatchValidationResult(score, warnings, status);
    }

    internal static float CrossValidateArtist(
        AcoustIdMatch match, SongMetadata track, float score, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(track.Artist))
            return score;

        var existingNorm = Normalize(track.Artist);
        var matchedNorm = Normalize(match.Artist);

        // When normalization strips everything (e.g. a symbol-only artist like "¥$"), fall back
        // to raw casefold so a wrong artist still trips the penalty instead of sailing through.
        if (string.IsNullOrEmpty(existingNorm) || string.IsNullOrEmpty(matchedNorm))
        {
            existingNorm = track.Artist.Trim().ToLowerInvariant();
            matchedNorm = match.Artist?.Trim().ToLowerInvariant() ?? string.Empty;
            if (string.IsNullOrEmpty(existingNorm) || string.IsNullOrEmpty(matchedNorm))
                return score;
        }

        if (!existingNorm.Contains(matchedNorm, StringComparison.Ordinal) &&
            !matchedNorm.Contains(existingNorm, StringComparison.Ordinal))
        {
            warnings.Add("artist_mismatch");
            return score * ArtistMismatchPenalty;
        }

        return score;
    }

    internal static float CrossValidateDuration(
        AcoustIdMatch match, SongMetadata track, float score, List<string> warnings)
    {
        if (track.DurationMs is null || match.RecordingDurationMs is null)
            return score;

        var deltaSeconds = Math.Abs(track.DurationMs.Value - match.RecordingDurationMs.Value) / 1000.0;
        if (deltaSeconds > DurationDeltaThresholdSeconds)
        {
            warnings.Add("duration_mismatch");
            return score * DurationMismatchPenalty;
        }

        return score;
    }

    internal static float CrossValidateTitle(
        AcoustIdMatch match, SongMetadata track, float score, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(track.Title))
            return score;

        var existingNorm = Normalize(track.Title);
        var matchedNorm = Normalize(match.Title);

        if (string.IsNullOrEmpty(existingNorm) || string.IsNullOrEmpty(matchedNorm))
            return score;

        if (!existingNorm.Contains(matchedNorm, StringComparison.Ordinal) &&
            !matchedNorm.Contains(existingNorm, StringComparison.Ordinal))
        {
            warnings.Add("title_mismatch");
            return score * TitleMismatchPenalty;
        }

        return score;
    }

    private static EnrichmentStatus DetermineStatus(float adjustedScore, List<string> warnings)
    {
        if (adjustedScore < DiscardThreshold)
            return EnrichmentStatus.NeedsReview;

        var hasBlockingWarning = warnings.Any(w => w != "multiple_candidates");
        if (adjustedScore >= MatchedThreshold && !hasBlockingWarning)
            return EnrichmentStatus.Matched;

        return EnrichmentStatus.NeedsReview;
    }

    internal static string Normalize(string value)
    {
        return TitleNormalizer.FoldDiacritics(value
            .ToLowerInvariant()
            .Replace("'", "")
            .Replace("\u2019", "")
            .Replace("\u2018", "")
            .Replace("\"", "")
            .Replace("\u201c", "")
            .Replace("\u201d", "")
            .Replace("-", " "))
            .Trim();
    }
}
