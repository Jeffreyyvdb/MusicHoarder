using FuzzySharp;

namespace MusicHoarder.Api.Matching;

/// <summary>Whether two names are one artist's, judged on spelling alone.</summary>
public static class ArtistNameMatch
{
    /// <summary>
    /// The same name, give or take spelling (a typo, punctuation, diacritics). Deliberately stricter
    /// than <see cref="CanonicalAlbumMatch.SameArtist"/>, which accepts one name plus whole words: that
    /// is right for an album credit ("Ms. Lauryn Hill"), and wrong for an artist's identity, where
    /// "Daft Punk Tribute Orchestra" lists a "Discovery" too and "Joey" is not "Joey Bada$$".
    /// A missing name is never the same as anything.
    /// </summary>
    public static bool SameSpelling(string? name, string? other, double threshold)
    {
        var left = TitleNormalizer.NormalizeForSearch(name);
        var right = TitleNormalizer.NormalizeForSearch(other);
        // A symbol-only name ("¥$") normalizes to nothing; the raw comparison is all there is.
        if (left.Length == 0 || right.Length == 0)
            return FuzzyTextMatch.Ratio(name, other) >= threshold;

        return left == right || Fuzz.Ratio(left, right) >= threshold;
    }
}
