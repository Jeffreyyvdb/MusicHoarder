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

    /// <summary>The numbers in a generic title ("Interlude II" → [2], "Intro" → []), or null for any other title.</summary>
    private static int[]? GenericTitleNumber(string title)
    {
        var words = Words(TitleNormalizer.NormalizeForSearch(title));
        if (words.Length == 0)
            words = Words(title.ToLowerInvariant());
        if (!words.Any(GenericTitleWords.Contains))
            return null;

        var numbers = new List<int>();
        foreach (var word in words.Where(w => !GenericTitleWords.Contains(w) && !NumberingWords.Contains(w)))
        {
            if (NumberOf(word) is not { } number)
                return null;
            numbers.Add(number);
        }

        return [.. numbers];
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

    private static bool SameTitle(string ours, string theirs, double threshold)
    {
        var left = TitleNormalizer.NormalizeForSearch(ours);
        var right = TitleNormalizer.NormalizeForSearch(theirs);
        if (left.Length == 0 || right.Length == 0)
            return TitleNormalizer.NormalizeArtistKey(ours) == TitleNormalizer.NormalizeArtistKey(theirs);

        return left == right || Fuzz.Ratio(left, right) >= threshold;
    }
}
