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
/// credit or its primary artist; "The Beat" is not "Fenix Flexin, Purps On The Beat"), its title is the
/// song's title, and the two agree on version (live, remix, …). A generic title ("Intro", "Skit #3")
/// names a track on most of an artist's albums, and a hit carries no album, so there the release year
/// must be known on both sides and agree.
/// </summary>
public static class GeniusHitMatcher
{
    private static readonly HashSet<string> GenericTitleWords =
        ["intro", "outro", "interlude", "skit", "prelude", "untitled"];

    /// <summary>The first acceptable hit in Genius's own order, or null.</summary>
    public static GeniusHit? Pick(
        IEnumerable<GeniusHit> hits, string? title, string? artist, int? year,
        double titleThreshold, double artistThreshold)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
            return null;

        var primary = ArtistCreditNormalizer.GetPrimaryArtist(artist);
        var expectedVersion = VersionQualifier.Detect(title);
        var needsYear = IsGenericTitle(title);

        return hits.FirstOrDefault(hit =>
            string.Equals(hit.LyricsState, "complete", StringComparison.OrdinalIgnoreCase)
            && !hit.Instrumental
            && (ArtistNameMatch.SameSpelling(artist, hit.PrimaryArtist, artistThreshold)
                || ArtistNameMatch.SameSpelling(primary, hit.PrimaryArtist, artistThreshold))
            && SameTitle(title, hit.Title, titleThreshold)
            && VersionQualifier.Compare(expectedVersion, VersionQualifier.Detect(hit.Title))
            && (!needsYear || (year is not null && year == hit.ReleaseYear)));
    }

    private static bool IsGenericTitle(string title)
    {
        var words = TitleNormalizer.NormalizeForSearch(title)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !w.All(char.IsAsciiDigit))
            .ToList();
        return words.Count > 0 && words.All(GenericTitleWords.Contains);
    }

    private static bool SameTitle(string ours, string theirs, double threshold)
    {
        var left = TitleNormalizer.NormalizeForSearch(ours);
        var right = TitleNormalizer.NormalizeForSearch(theirs);
        if (left.Length == 0 || right.Length == 0)
            return TitleNormalizer.NormalizeArtistKey(ours) == TitleNormalizer.NormalizeArtistKey(theirs);

        return left == right || Fuzz.Ratio(left, right) >= threshold;
    }
}
