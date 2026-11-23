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
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(other))
            return false;

        var left = SpellingKey(name);
        var right = SpellingKey(other);
        return left == right || Fuzz.Ratio(left, right) >= threshold;
    }

    private static string SpellingKey(string name)
    {
        var normalized = TitleNormalizer.NormalizeForSearch(name);
        return normalized.Length > 0 ? normalized : name.Trim().ToLowerInvariant();
    }
}
