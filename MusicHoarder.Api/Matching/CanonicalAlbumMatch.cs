using FuzzySharp;
using MusicHoarder.Api.Library;

namespace MusicHoarder.Api.Matching;

/// <summary>
/// Whether a provider's album is the album the owner's songs are tagged with.
/// <para>
/// A canonical album is found by searching "artist album", and a search always answers with
/// something. An album no catalog carries — Kanye West's unreleased "CHIRAQ" — came back as a Fat Joe
/// single, the Fat Joe single's search as a cumbia EP, and that EP's as "20 Éxitos Bailables". Every
/// reader trusts a canonical album: the album page's missing tracks, the split-album heal's album
/// artist, album completion's downloads. So an answer counts only when it shows it is this album:
/// its title resembles the owner's album title, and its artist resembles the owner's album artist or
/// its tracklist holds one of the owner's tracks. The second way in is how a featured artist's song
/// legitimately sits on someone else's album or on a compilation.
/// </para>
/// <para>
/// A missing title or artist is no evidence either way and does not fail the check, the same
/// convention <see cref="FuzzyTextMatch.Ratio"/>'s callers follow elsewhere.
/// </para>
/// <para>
/// Names are compared more strictly than <see cref="FuzzyTextMatch.Ratio"/> compares them. Its partial
/// matching scores a short string found anywhere inside a long one, so rapper Ka's "Die Man" scored
/// 90 against a German audiobook — "Die Man" in "… oder Die Manen der Familie Prischinger", and "Ka"
/// in "Vea Kaiser". Here two names are the same when they are close as whole strings (a spelling
/// variant, a typo) or one is the other plus whole words ("Graduation" and "Graduation (Alternative
/// Business Partners)", "Lauryn Hill" and "Ms. Lauryn Hill").
/// </para>
/// <para>
/// Callers pass only the tracks the owner brought in: an album-fill download exists because of a
/// canonical album and cannot vouch for it. Album completion filled 2 Chainz's "So Help Me God!" into
/// a Kanye leak of that name, and those downloads then made 2 Chainz's tracklist look like the
/// owner's.
/// </para>
/// </summary>
public static class CanonicalAlbumMatch
{
    public static bool IsSameAlbum(
        string? album,
        string? albumArtist,
        IEnumerable<string?> ownedTitles,
        string? candidateTitle,
        string? candidateArtist,
        IEnumerable<string?> candidateTrackTitles,
        double titleThreshold,
        double artistThreshold)
        => TitleMatches(album, candidateTitle, titleThreshold)
            && (SameArtist(albumArtist, candidateArtist, artistThreshold)
                || SharesATrack(album, ownedTitles, candidateTrackTitles, titleThreshold));

    public static bool TitleMatches(string? album, string? candidateTitle, double threshold)
        => Resembles(album, candidateTitle, threshold) is not false;

    /// <summary>Two Various Artists spellings are the same artist, whatever language they are in.</summary>
    public static bool SameArtist(string? artist, string? candidateArtist, double threshold)
    {
        if (IsVariousArtists(artist) && IsVariousArtists(candidateArtist))
            return true;
        return Resembles(artist, candidateArtist, threshold) is not false;
    }

    /// <summary>
    /// Whether one of the owner's tracks is on the candidate's tracklist. A title track does not count:
    /// it restates the album title the other check already compared, so a one-track single named like
    /// the album would otherwise prove itself — Juice WRLD's leak "What Is Love?" against Jaymes
    /// Young's single of that name.
    /// </summary>
    public static bool SharesATrack(
        string? album, IEnumerable<string?> ownedTitles, IEnumerable<string?> candidateTrackTitles, double threshold)
    {
        var candidates = candidateTrackTitles
            .Where(t => !string.IsNullOrWhiteSpace(t) && !IsTitleTrack(album, t, threshold))
            .ToList();
        if (candidates.Count == 0)
            return false;

        return ownedTitles
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Any(owned => candidates.Any(c => Resembles(owned, c, threshold) is true));
    }

    private static bool IsTitleTrack(string? album, string? title, double threshold) =>
        Resembles(album, title, threshold) is true;

    /// <summary>Null when one side has no text to compare.</summary>
    private static bool? Resembles(string? a, string? b, double threshold)
    {
        var left = TitleNormalizer.NormalizeForSearch(a);
        var right = TitleNormalizer.NormalizeForSearch(b);
        // A symbol-only name ("¥$") normalizes to nothing; the raw comparison is all there is.
        if (left.Length == 0 || right.Length == 0)
            return FuzzyTextMatch.Ratio(a, b) is { } raw ? raw >= threshold : null;

        if (left == right || Fuzz.Ratio(left, right) >= threshold)
            return true;

        var leftWords = left.Split(' ');
        var rightWords = right.Split(' ');
        var (fewer, more) = leftWords.Length <= rightWords.Length ? (leftWords, rightWords) : (rightWords, leftWords);
        return fewer.All(more.Contains);
    }

    private static bool IsVariousArtists(string? artist) =>
        !string.IsNullOrWhiteSpace(artist) && DestinationPathResolver.IsVariousArtistsSentinel(artist);
}
