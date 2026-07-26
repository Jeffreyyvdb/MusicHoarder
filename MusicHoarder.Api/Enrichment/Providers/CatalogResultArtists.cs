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
    /// track's primary artist so it still lands in a usable destination folder.</item>
    /// </list>
    /// </summary>
    public static ResolvedArtists Resolve(SongMetadata song, string? trackArtist)
    {
        var effectiveArtist = string.IsNullOrWhiteSpace(trackArtist) ? song.Artist : trackArtist;
        var albumArtist = !string.IsNullOrWhiteSpace(song.AlbumArtist)
            ? song.AlbumArtist
            : ArtistCreditNormalizer.GetPrimaryArtist(effectiveArtist) ?? effectiveArtist;
        return new ResolvedArtists(effectiveArtist, albumArtist);
    }
}
