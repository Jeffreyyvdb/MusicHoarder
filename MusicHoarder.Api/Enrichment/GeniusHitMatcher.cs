using System.Text.RegularExpressions;
using FuzzySharp;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Metadata;

namespace MusicHoarder.Api.Enrichment;

public sealed record GeniusHit(
    int Id,
    string Title,
    string PrimaryArtist,
    string? LyricsState,
    bool Instrumental,
    int? ReleaseYear);

/// <summary>
/// Chooses which of Genius's search hits, if any, is the page for a song. A search always answers, and
/// the first hit is often another page: a translation ("Genius English Translations"), a remix, an
/// unreleased cut. A hit counts only when its primary artist is the song's artist by spelling (the full
/// credit or its primary artist), its title is the song's title, and the two agree on version (live,
/// remix, …). For a generic title ("Intro", "Skit #3", "Interlude II") the release year must be known on
/// both sides and agree, and the two titles must carry the same number.
/// </summary>
public static partial class GeniusHitMatcher
{
    private static readonly HashSet<string> GenericTitleWords =
        ["intro", "outro", "interlude", "skit", "prelude", "untitled"];

    private static readonly HashSet<string> NumberingWords = ["the", "pt", "part"];

    private static readonly Dictionary<char, int> RomanDigits =
        new() { ['i'] = 1, ['v'] = 5, ['x'] = 10, ['l'] = 50 };

    /// <summary>The first acceptable hit in Genius's own order, or null.</summary>
    public static GeniusHit? Pick(
        IEnumerable<GeniusHit> hits, string? title, string? artist, int? year,
        double titleThreshold, double artistThreshold)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
            return null;

        var primary = ArtistCreditNormalizer.GetPrimaryArtist(artist);
        var expectedVersion = VersionQualifier.Detect(title);
        var genericNumber = GenericTitleNumber(title);

        return hits.FirstOrDefault(hit =>
            string.Equals(hit.LyricsState, "complete", StringComparison.OrdinalIgnoreCase)
            && !hit.Instrumental
            && (ArtistNameMatch.SameSpelling(artist, hit.PrimaryArtist, artistThreshold)
                || ArtistNameMatch.SameSpelling(primary, hit.PrimaryArtist, artistThreshold))
            && SameTitle(title, hit.Title, titleThreshold)
            && VersionQualifier.Compare(expectedVersion, VersionQualifier.Detect(hit.Title))
            && (genericNumber is null
                || (year is not null && year == hit.ReleaseYear
                    && GenericTitleNumber(hit.Title) is { } hitNumber && hitNumber.SequenceEqual(genericNumber))));
    }

    /// <summary>The title without a featured-artist credit, in brackets or trailing.</summary>
    public static string WithoutFeaturing(string title) => FeaturingCredit().Replace(title, string.Empty).Trim();

    /// <summary>
    /// The numbers in a generic title ("Interlude II" → [2], "Skit (1)" → [1], "Intro" → []), or null for
    /// any other title. Whether it is generic is read without bracketed text, its numbers with it.
    /// </summary>
    private static int[]? GenericTitleNumber(string title)
    {
        var words = Words(TitleNormalizer.NormalizeForSearch(title));
        if (words.Length == 0)
            words = Words(title.ToLowerInvariant());
        var generic = words.Any(GenericTitleWords.Contains)
            && words.All(w => GenericTitleWords.Contains(w) || NumberingWords.Contains(w) || NumberOf(w) is not null);
        if (!generic)
            return null;

        return [.. Words(WithoutFeaturing(title).ToLowerInvariant()).Select(NumberOf).OfType<int>()];
    }

    private static string[] Words(string text) =>
        NonWordRun().Split(text).Where(w => w.Length > 0).ToArray();

    private static int? NumberOf(string word)
    {
        if (int.TryParse(word, out var number))
            return number;
        if (!word.All(RomanDigits.ContainsKey))
            return null;

        var total = 0;
        for (var i = 0; i < word.Length; i++)
        {
            var value = RomanDigits[word[i]];
            total += i + 1 < word.Length && value < RomanDigits[word[i + 1]] ? -value : value;
        }

        return total;
    }

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWordRun();

    [GeneratedRegex(@"\s*[\(\[]\s*(feat\.?|ft\.?|featuring|with)\s[^\)\]]*[\)\]]|\s+(feat\.?|ft\.?|featuring)\s.*$", RegexOptions.IgnoreCase)]
    private static partial Regex FeaturingCredit();

    private static bool SameTitle(string ours, string theirs, double threshold)
    {
        var left = TitleNormalizer.NormalizeForSearch(ours);
        var right = TitleNormalizer.NormalizeForSearch(theirs);
        if (left.Length == 0 || right.Length == 0)
            return TitleNormalizer.NormalizeArtistKey(ours) == TitleNormalizer.NormalizeArtistKey(theirs);

        return left == right || Fuzz.Ratio(left, right) >= threshold;
    }
}
