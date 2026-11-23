using FuzzySharp;

namespace MusicHoarder.Api.Matching;

/// <summary>Whether two names are one artist's, judged on spelling alone.</summary>
public static class ArtistNameMatch
{
    /// <summary>
    /// The same name, give or take spelling (a typo, punctuation, diacritics). A name made only of
    /// symbols ("¥$") matches only the same symbols. A missing name is never the same as anything.
    /// </summary>
    public static bool SameSpelling(string? name, string? other, double threshold)
    {
        var left = TitleNormalizer.NormalizeForSearch(name);
        var right = TitleNormalizer.NormalizeForSearch(other);
        if (left.Length > 0 && right.Length > 0)
            return left == right || Fuzz.Ratio(left, right) >= threshold;

        return SymbolsOnly(name) is { } symbols && symbols == SymbolsOnly(other);
    }

    private static string? SymbolsOnly(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Any(char.IsLetterOrDigit)
            ? null
            : string.Concat(name.Where(c => !char.IsWhiteSpace(c)));
}
