using FuzzySharp;

namespace MusicHoarder.Api.Matching;

/// <summary>
/// Shared artist/title fuzzy comparison used by the name-based enrichment providers.
/// </summary>
public static class FuzzyTextMatch
{
    /// <summary>
    /// Fuzzy similarity (0..100) between two artist/title strings.
    /// <para>
    /// Both sides are first run through <see cref="TitleNormalizer.NormalizeForSearch"/>.
    /// When normalization reduces a <i>non-empty</i> raw value to empty — e.g. a symbol-only
    /// artist such as "¥$" whose characters are all stripped as punctuation — we fall back to a
    /// casefolded comparison of the raw text rather than treating the pair as a perfect match.
    /// That avoids the bug where a wrong candidate ("RAREKID") silently scored as a 100% artist
    /// agreement just because the source artist normalized away.
    /// </para>
    /// Returns <c>null</c> when one side has no comparable text at all (genuinely unknown), so
    /// callers can decide how to handle a missing signal instead of assuming agreement.
    /// </summary>
    public static double? Ratio(string? source, string? candidate)
    {
        var sourceNorm = TitleNormalizer.NormalizeForSearch(source);
        var candNorm = TitleNormalizer.NormalizeForSearch(candidate);

        if (sourceNorm.Length > 0 && candNorm.Length > 0)
            return Fuzz.WeightedRatio(sourceNorm, candNorm);

        var sourceRaw = source?.Trim().ToLowerInvariant();
        var candRaw = candidate?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(sourceRaw) && !string.IsNullOrEmpty(candRaw))
            return Fuzz.WeightedRatio(sourceRaw, candRaw);

        return null;
    }
}
