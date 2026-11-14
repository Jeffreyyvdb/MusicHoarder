using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Artists;

/// <summary>Loads the songs of one artist that a discography decision reads.</summary>
public static class ArtistSongs
{
    /// <summary>
    /// The live, non-duplicate songs credited to <paramref name="artistName"/> in <paramref name="songs"/>
    /// — which the caller scopes: the ambient owner filter in a request, an explicit owner in the
    /// background. The database narrows by a case-insensitive substring of the name first (so this
    /// never reads the whole library), then <see cref="ArtistDiscography.IsByArtist"/> decides exactly.
    /// </summary>
    public static async Task<List<ArtistSongFacts>> LoadAsync(
        IQueryable<SongMetadata> songs, string artistName, CancellationToken ct)
    {
        var key = TitleNormalizer.NormalizeForSearch(artistName);
        var needle = artistName.Trim().ToLowerInvariant();
        if (key.Length == 0 || needle.Length == 0) return [];

        var rows = await songs
            .AsNoTracking()
            .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic && !s.IsDuplicate)
            .Where(s => (s.AlbumArtist != null && s.AlbumArtist.ToLower().Contains(needle))
                || (s.Artist != null && s.Artist.ToLower().Contains(needle))
                || (s.Artists != null && s.Artists.ToLower().Contains(needle)))
            .Select(s => new ArtistSongFacts(
                s.Id, s.Title, s.Album, s.AlbumArtist, s.Artist, s.Artists, s.MusicBrainzId,
                s.DiscNumber, s.TrackNumber, s.AcquisitionIntent))
            .ToListAsync(ct);

        return rows.Where(s => ArtistDiscography.IsByArtist(s, key)).ToList();
    }
}
