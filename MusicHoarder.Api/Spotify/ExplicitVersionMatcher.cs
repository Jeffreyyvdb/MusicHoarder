using System.Text.RegularExpressions;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Metadata;

namespace MusicHoarder.Api.Spotify;

/// <summary>
/// Finds the explicit edition of a clean Spotify track. Labels publish many songs twice, the explicit
/// master and a clean edit with the words muted, as separate Spotify tracks with their own ISRCs on
/// separate album editions, sharing the title, the artists and (to within a second or so) the length.
/// Anything that acquires audio by Spotify id gets whichever edition that id names, so a clean id
/// quietly yields a censored file.
/// </summary>
public static partial class ExplicitVersionMatcher
{
    /// <summary>
    /// How far apart two editions' lengths may be. A clean edit mutes words in place, so it runs as long
    /// as the explicit master; a wider gap means a different cut (radio edit, extended mix), not the same
    /// song with the words back in.
    /// </summary>
    public const int MaxDurationDeltaMs = 3_000;

    /// <summary>The catalog search that surfaces a track's other editions.</summary>
    public static string SearchQuery(SpotifyCatalogTrack track)
    {
        var artist = MultiValue.Split(track.Artists).FirstOrDefault() ?? track.Artist;
        return $"track:{StripCleanMarker(track.Title)} artist:{artist}".Trim();
    }

    /// <summary>
    /// The explicit edition of <paramref name="track"/> among <paramref name="candidates"/>, or null when
    /// <paramref name="track"/> is already explicit or no candidate is the same song: same title (a
    /// "(Clean)" marker aside), same artists, the same identity-changing qualifiers (an explicit live
    /// take is a different recording), and a length within <see cref="MaxDurationDeltaMs"/>. The
    /// closest length wins; ties keep the catalog's relevance order.
    /// </summary>
    public static SpotifyCatalogTrack? FindExplicitEdition(
        SpotifyCatalogTrack track, IEnumerable<SpotifyCatalogTrack> candidates)
    {
        if (track.Explicit)
            return null;

        var title = NormalizeTitle(track.Title);
        var artist = TitleNormalizer.NormalizeForSearch(track.Artist);
        var qualifiers = VersionQualifier.Detect(track.Title);
        if (title.Length == 0 || artist.Length == 0)
            return null;

        return candidates
            .Where(c => c.Explicit
                && !string.Equals(c.Id, track.Id, StringComparison.Ordinal)
                && NormalizeTitle(c.Title) == title
                && TitleNormalizer.NormalizeForSearch(c.Artist) == artist
                && VersionQualifier.Compare(qualifiers, VersionQualifier.Detect(c.Title))
                && DurationDelta(track, c) <= MaxDurationDeltaMs)
            .OrderBy(c => DurationDelta(track, c))
            .FirstOrDefault();
    }

    // Unknown lengths can't prove two editions match, so they never qualify.
    private static int DurationDelta(SpotifyCatalogTrack a, SpotifyCatalogTrack b) =>
        a.DurationMs > 0 && b.DurationMs > 0 ? Math.Abs(a.DurationMs - b.DurationMs) : int.MaxValue;

    private static string NormalizeTitle(string title) => TitleNormalizer.NormalizeForSearch(StripCleanMarker(title));

    private static string StripCleanMarker(string title) => CleanMarkerPattern().Replace(title, "").Trim();

    // "Song (Clean)", "Song [Edited Version]", "Song - Clean": the clean edition's own label, which the
    // explicit edition never carries.
    [GeneratedRegex(@"\s*(?:[\(\[]\s*(?:clean|edited|censored)(?:\s+version)?\s*[\)\]]|\s-\s(?:clean|edited|censored)(?:\s+version)?\s*$)",
        RegexOptions.IgnoreCase)]
    private static partial Regex CleanMarkerPattern();
}
