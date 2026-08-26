using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Library;

/// <summary>
/// Spotify's own save dates for the caller's library, loaded once per request.
///
/// <para>
/// Two sources, because neither is complete on its own. A <b>wishlist link</b> exists only for songs
/// the download pipeline touched, so a track that was already in the library — or whose like matched
/// a different file than the wishlist downloaded — has none. The <b>liked-songs match cache</b>
/// carries Spotify's added-at for every liked song, and is keyed two ways: by the song the sweep
/// matched (covers unenriched files), and by Spotify track id against the song's enriched
/// <c>SpotifyId</c>, because the cache re-points between duplicate copies as sweeps re-run and the id
/// join keeps every copy's date stable instead of only the currently-matched one.
/// </para>
///
/// <para>
/// Shared by <c>GET /songs</c> and <c>GET /api/albums</c> deliberately: the album grid's "Recently
/// added" and the track list's sort read the same dates, and two copies of this would let them
/// disagree about the same track.
/// </para>
/// </summary>
public sealed class SpotifySaveDates
{
    private readonly Dictionary<int, WishlistLink> _links;
    private readonly Dictionary<int, DateTime> _bySongId;
    private readonly Dictionary<string, DateTime> _bySpotifyId;

    private SpotifySaveDates(
        Dictionary<int, WishlistLink> links,
        Dictionary<int, DateTime> bySongId,
        Dictionary<string, DateTime> bySpotifyId)
    {
        _links = links;
        _bySongId = bySongId;
        _bySpotifyId = bySpotifyId;
    }

    /// <summary>
    /// Two small owner-scoped reads rather than a join per row — there are far fewer wishlist items
    /// than songs. Items that were <c>SkippedOwned</c> link too: that is how a track already in the
    /// library still reports the date it was liked on Spotify.
    /// </summary>
    public static async Task<SpotifySaveDates> LoadAsync(MusicHoarderDbContext db, CancellationToken ct)
    {
        var sources = await db.WishlistSources
            .AsNoTracking()
            .Select(s => new { s.Id, s.SourceType, s.Name })
            .ToDictionaryAsync(s => s.Id, s => (s.SourceType, s.Name), ct);

        var links = (await db.WishlistItems
                .AsNoTracking()
                .Where(w => w.DownloadedSongId != null)
                .Select(w => new { SongId = w.DownloadedSongId!.Value, w.WishlistSourceId, w.SpotifyAddedAtUtc, w.SourceUrl, w.Origin, w.Album })
                .ToListAsync(ct))
            .GroupBy(w => w.SongId)
            .ToDictionary(
                g => g.Key,
                g => SongOriginResolver.Best(g.Select(w =>
                {
                    var source = w.WishlistSourceId is { } id && sources.TryGetValue(id, out var s)
                        ? ((WishlistSourceType?)s.SourceType, s.Name)
                        : (null, null);
                    return new WishlistLink(source.Item1, source.Item2, w.SourceUrl, w.SpotifyAddedAtUtc, w.Origin, w.Album);
                })));

        var likedRows = await db.SpotifyTrackLibraryMatches
            .AsNoTracking()
            .Where(m => m.Source == SpotifyLibraryComparisonService.SourceLikedSync
                && m.SpotifyAddedAtUtc != null)
            .Select(m => new { m.SpotifyTrackId, m.MatchedSongId, AddedAt = m.SpotifyAddedAtUtc!.Value })
            .ToListAsync(ct);

        var bySongId = likedRows
            .Where(m => m.MatchedSongId != null)
            .GroupBy(m => m.MatchedSongId!.Value)
            .ToDictionary(g => g.Key, g => g.Min(m => m.AddedAt));
        var bySpotifyId = likedRows
            .GroupBy(m => m.SpotifyTrackId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Min(m => m.AddedAt), StringComparer.OrdinalIgnoreCase);

        return new SpotifySaveDates(links, bySongId, bySpotifyId);
    }

    /// <summary>The wishlist link that best describes a song, for provenance resolution. Null when
    /// nothing points at it.</summary>
    public WishlistLink? LinkFor(int songId) =>
        _links.TryGetValue(songId, out var link) ? link : null;

    /// <summary>
    /// The song's Spotify save date: the earliest of the wishlist link's date and both match-cache
    /// keys. Both are Spotify timestamps, and the earliest is the real "when did I save this" moment.
    /// </summary>
    /// <param name="linkAdded">
    /// The date from the resolved origin. Callers pass the link's date for "Spotify knows about
    /// this", and only a Liked-Songs link's date for "I saved this" — a playlist add carries a date
    /// too, and letting it through would make a track the user never saved read as liked.
    /// </param>
    public DateTime? SaveDateFor(int songId, string? spotifyId, DateTime? linkAdded)
    {
        var bySong = _bySongId.TryGetValue(songId, out var songDate) ? songDate : (DateTime?)null;
        var byId = spotifyId is not null && _bySpotifyId.TryGetValue(spotifyId, out var idDate)
            ? idDate
            : (DateTime?)null;
        return Earliest(Earliest(linkAdded, bySong), byId);
    }

    private static DateTime? Earliest(DateTime? a, DateTime? b) =>
        a is null ? b : b is null ? a : a < b ? a : b;
}
