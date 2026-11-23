using FuzzySharp;

namespace MusicHoarder.Api.Matching;

/// <summary>Whether two names are one artist's, judged on spelling alone.</summary>
public static class ArtistNameMatch
{
    /// <summary>
    /// The same name, give or take spelling (a typo, punctuation, diacritics). A missing name is never
    /// the same as anything.
    /// </summary>
    public static bool SameSpelling(string? name, string? other, double threshold)
    {
        var left = TitleNormalizer.NormalizeForSearch(name);
        var right = TitleNormalizer.NormalizeForSearch(other);
        if (left.Length == 0 || right.Length == 0)
            return FuzzyTextMatch.Ratio(name, other) >= threshold;

        return left == right || Fuzz.Ratio(left, right) >= threshold;
    }
}
