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
    /// background. Reads a few narrow columns of every song and lets
    /// <see cref="ArtistDiscography.IsByArtist"/> decide, with no name filter in SQL: the rule folds
    /// diacritics and punctuation ("Beyonce" is "Beyoncé", "ACDC" is "AC/DC"), and a substring filter
    /// in front of it dropped exactly those songs, which then looked missing and were downloaded again.
    /// </summary>
    public static async Task<List<ArtistSongFacts>> LoadAsync(
        IQueryable<SongMetadata> songs, string artistName, CancellationToken ct)
    {
        var key = TitleNormalizer.NormalizeForSearch(artistName);
        if (key.Length == 0) return [];

        var rows = await songs
            .AsNoTracking()
            .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic && !s.IsDuplicate)
            .Select(s => new ArtistSongFacts(
                s.Id, s.Title, s.Album, s.AlbumArtist, s.Artist, s.Artists, s.MusicBrainzId,
                s.DiscNumber, s.TrackNumber, s.AcquisitionIntent))
            .ToListAsync(ct);

        return rows.Where(s => ArtistDiscography.IsByArtist(s, key)).ToList();
    }
}
