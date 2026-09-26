using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment.Providers;

/// <summary>
/// The shared artist / album-artist derivation for the catalog (name-based) enrichment providers
/// (Deezer, Apple Music, Spotify). All three map a catalog hit onto an
/// <see cref="EnrichmentProviderResult"/> and must apply the same rule for the two artist fields, so
/// keeping it here stops the identical block (and its load-bearing comment) from being copied into
/// three <c>BuildResult</c> methods that then have to change in lock-step.
/// </summary>
public static class CatalogResultArtists
{
    /// <summary>The two artist fields a catalog match contributes to a song.</summary>
    public readonly record struct ResolvedArtists(string? Artist, string? AlbumArtist);

    /// <summary>
    /// Derives the effective (track) artist and the album-artist for a catalog match:
    /// <list type="bullet">
    /// <item><b>Artist</b> is the catalog track's artist, falling back to the song's own tag when the
    /// catalog omits it.</item>
    /// <item><b>Album-artist</b> is an album-level field, so it is never synthesized from the
    /// <i>track</i> artist credit — on compilations/collabs that credit is a featured guest, and for
    /// comma-names ("Tyler, The Creator") <see cref="ArtistCreditNormalizer.GetPrimaryArtist"/> would
    /// truncate it, both of which split one album into several. The song's curated
    /// <see cref="SongMetadata.AlbumArtist"/> wins; only a genuinely untagged file falls back to the
    /// track's primary artist so it still lands in a usable destination folder. That primary is the
    /// first of <paramref name="trackArtists"/> when the catalog publishes its discrete list, and only
    /// otherwise a parse of the display credit.</item>
    /// </list>
    /// </summary>
    /// <param name="trackArtists">The catalog track's discrete artist names (';'-joined), when the
    /// display credit was joined from a per-artist list — Spotify's <c>artists[]</c>, which
    /// SpotifyCatalogSearchService joins with ", ". Re-parsing that join cannot tell the join from a
    /// name: "Tyler, The Creator" alone came back "Tyler", "Chase &amp; Status, Plan B" came back
    /// "Chase".</param>
    public static ResolvedArtists Resolve(SongMetadata song, string? trackArtist, string? trackArtists = null)
    {
        var fromCatalog = !string.IsNullOrWhiteSpace(trackArtist);
        var effectiveArtist = fromCatalog ? trackArtist : song.Artist;
        // The discrete list only describes the catalog's credit, so it is ignored when the song's
        // own artist stood in for a blank one.
        var lead = fromCatalog ? MultiValue.Split(trackArtists).FirstOrDefault() : null;
        var albumArtist = !string.IsNullOrWhiteSpace(song.AlbumArtist)
            ? song.AlbumArtist
            : lead ?? ArtistCreditNormalizer.GetPrimaryArtist(effectiveArtist) ?? effectiveArtist;
        return new ResolvedArtists(effectiveArtist, albumArtist);
    }
}
