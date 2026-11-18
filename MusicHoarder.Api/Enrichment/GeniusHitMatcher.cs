using FuzzySharp;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Metadata;

namespace MusicHoarder.Api.Enrichment;

public sealed record GeniusHit(
    int Id,
    string Title,
    string PrimaryArtist,
    string? LyricsState,
    bool Instrumental);

/// <summary>
/// Chooses which of Genius's search hits, if any, is the page for a song. A search always answers, and
/// the first hit is often another page: a translation ("Genius English Translations"), a remix, an
/// unreleased cut. A hit counts only when its primary artist is the song's artist, its title is the
/// song's title, and the two agree on version (live, remix, …).
/// </summary>
public static class GeniusHitMatcher
{
    /// <summary>The first acceptable hit in Genius's own order, or null.</summary>
    public static GeniusHit? Pick(
        IEnumerable<GeniusHit> hits, string? title, string? artist, double titleThreshold, double artistThreshold)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
            return null;

        var primary = ArtistCreditNormalizer.GetPrimaryArtist(artist);
        var expectedVersion = VersionQualifier.Detect(title);

        return hits.FirstOrDefault(hit =>
            string.Equals(hit.LyricsState, "complete", StringComparison.OrdinalIgnoreCase)
            && !hit.Instrumental
            && (CanonicalAlbumMatch.SameArtist(artist, hit.PrimaryArtist, artistThreshold)
                || (primary is not null && CanonicalAlbumMatch.SameArtist(primary, hit.PrimaryArtist, artistThreshold)))
            && SameTitle(title, hit.Title, titleThreshold)
            && VersionQualifier.Compare(expectedVersion, VersionQualifier.Detect(hit.Title)));
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
