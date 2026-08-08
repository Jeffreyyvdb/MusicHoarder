using MusicHoarder.Api.Matching;

namespace MusicHoarder.Api.Artwork;

/// <summary>
/// Accepts or rejects a text-search hit (album or artist) against the identity we searched for, so
/// artwork fallbacks never trust provider result order — a fuzzy hit on a "Best 50 Dance Hits"
/// compilation must not become the cover of a real album. Verification is deliberately biased
/// toward false negatives: a rejected hit falls through to the next provider, a wrong accept
/// bakes the wrong art into the library.
/// </summary>
public static class ArtworkMatchVerifier
{
    /// <summary>
    /// True when the candidate album plausibly IS the album we searched for: titles must be equal
    /// (or an edition-qualified variant of each other), and the album artists must agree. Without a
    /// query artist to compare (loose files), only an exact title match is accepted.
    /// </summary>
    public static bool IsAlbumMatch(string? queryArtist, string? queryAlbum, string? candidateArtist, string? candidateAlbum)
    {
        var album = TitleNormalizer.NormalizeForSearch(queryAlbum);
        var candAlbum = TitleNormalizer.NormalizeForSearch(candidateAlbum);
        if (album.Length == 0 || candAlbum.Length == 0)
            return false;

        // Same title, or an edition-qualified variant ("album deluxe edition" ↔ "album"): one side
        // must extend the other at a word boundary. (Parenthesized qualifiers are already stripped
        // by the normalizer.)
        if (album != candAlbum && !StartsAtWordBoundary(album, candAlbum) && !StartsAtWordBoundary(candAlbum, album))
            return false;

        var artist = TitleNormalizer.NormalizeForDedup(queryArtist);
        var candArtist = TitleNormalizer.NormalizeForDedup(candidateArtist);
        if (artist.Length == 0)
            return album == candAlbum;
        if (candArtist.Length == 0)
            return false;

        // Credit joiners differ per catalog ("A & B" / "A, B" / "A feat. B"), so accept when one
        // side's name tokens are a subset of the other's. "Various Artists" shares no tokens with a
        // real artist, so compilations still fail here.
        return artist == candArtist || TokensSubset(artist, candArtist) || TokensSubset(candArtist, artist);
    }

    /// <summary>
    /// True when the candidate artist IS the artist we searched for. Strict normalized equality:
    /// portraits have no second signal (like an album title) to lean on, and a prefix rule would
    /// let "Drake" claim "Drake Bell"'s portrait.
    /// </summary>
    public static bool IsArtistMatch(string? queryName, string? candidateName)
    {
        var query = TitleNormalizer.NormalizeForDedup(queryName);
        var candidate = TitleNormalizer.NormalizeForDedup(candidateName);
        return query.Length > 0 && query == candidate;
    }

    private static bool StartsAtWordBoundary(string longer, string prefix) =>
        longer.Length > prefix.Length
        && longer.StartsWith(prefix, StringComparison.Ordinal)
        && longer[prefix.Length] == ' ';

    private static readonly string[] JoinerTokens = ["and", "the"];

    private static bool TokensSubset(string subset, string superset)
    {
        var subTokens = subset.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !JoinerTokens.Contains(t))
            .ToArray();
        if (subTokens.Length == 0)
            return false;

        var superTokens = superset.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return subTokens.All(superTokens.Contains);
    }
}
