using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Library;

/// <summary>
/// The logical-album key used to recognize that two songs belong to the same real album even when
/// per-song enrichment gave them divergent metadata. The destination-folder key the builder
/// reconciles on is derived from the enriched <c>Album</c>/<c>Year</c>/<c>AlbumArtist</c>, so a
/// divergence in any of those puts siblings in different folders and the folder-level vote never
/// sees them together — this key is what does.
/// <para>
/// Year is deliberately excluded (divergent year is the most common split). Album and artist are
/// compared via <see cref="TitleNormalizer.NormalizeForSearch"/>, which strips parenthesized
/// qualifiers — so the <see cref="VersionQualifier"/> flags are appended to the album key as an
/// edition discriminator, keeping "X (Deluxe)" and "X (Live)" out of plain "X"'s group:
/// under-merging falls back to today's behavior, over-merging is the only dangerous direction.
/// <see cref="SongMetadata.MusicBrainzReleaseGroupId"/> is NOT used as the key: one MusicBrainz
/// release group contains deluxe/standard/remaster releases (over-merges) and it's frequently null.
/// </para>
/// </summary>
public sealed record AlbumGroupKey(Guid OwnerUserId, string ArtistKey, string AlbumKey)
{
    /// <summary>
    /// Sentinel artist key for genuine Various-Artists compilations, mirroring the
    /// <see cref="DestinationPathResolver"/> routing so a compilation groups by album under one
    /// key regardless of which VA spelling each track's provider used. The \u0001 prefix can't
    /// collide with any normalized artist name.
    /// </summary>
    public const string VariousArtistsKey = "\u0001va";

    /// <summary>
    /// Computes the logical-album key for a song, or null when the song has no usable album/artist
    /// to group on. Pure key computation — eligibility (deleted/synthetic/duplicate/unreleased/
    /// non-Matched) is the caller's filter, matching the builder's candidate predicate.
    /// </summary>
    public static AlbumGroupKey? For(SongMetadata song)
    {
        ArgumentNullException.ThrowIfNull(song);

        var albumKey = ComputeAlbumKey(song.Album);
        if (albumKey.Length == 0)
        {
            return null;
        }

        var artistKey = DestinationPathResolver.IsVariousArtists(song)
            ? VariousArtistsKey
            : ComputeArtistKey(song.AlbumArtist ?? ArtistCreditNormalizer.GetPrimaryArtist(song.Artist) ?? song.Artist);
        if (artistKey.Length == 0)
        {
            return null;
        }

        return new AlbumGroupKey(song.OwnerUserId, artistKey, albumKey);
    }

    /// <summary>
    /// Normalized artist key; VA sentinel spellings ("Various Artists", "VA", …) all map to
    /// <see cref="VariousArtistsKey"/> so an endpoint passing the display artist matches the
    /// song-side key.
    /// </summary>
    public static string ComputeArtistKey(string? artist)
    {
        if (!string.IsNullOrWhiteSpace(artist) && DestinationPathResolver.IsVariousArtistsSentinel(artist))
        {
            return VariousArtistsKey;
        }

        return TitleNormalizer.NormalizeForSearch(artist);
    }

    /// <summary>Normalized album title + edition-qualifier discriminator; empty when no usable title.</summary>
    public static string ComputeAlbumKey(string? album)
    {
        var normalized = TitleNormalizer.NormalizeForSearch(album);
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        // Pass the album as the *title* argument so the full qualifier set (incl. Live) is
        // detected, not just the album-masked Remaster|Deluxe|Extended subset.
        return $"{normalized}|{(int)VersionQualifier.Detect(album)}";
    }
}
