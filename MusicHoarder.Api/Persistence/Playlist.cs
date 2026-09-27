using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// A playlist in MusicHoarder: an ordered list of library songs an account plays, owned by that
/// account. Two kinds share this row:
/// <list type="bullet">
/// <item><b>Native</b> (<see cref="WishlistSourceId"/> null) — made here; every track is a
/// <see cref="PlaylistEntry"/>.</item>
/// <item><b>Source-backed</b> — the playlist of a collected Spotify / Deezer / YouTube playlist (or
/// Spotify Liked Songs). Its synced tracks are not copied here: they are read from the source's
/// <see cref="WishlistSourceTrack"/> membership at request time, so they follow the remote list. Its
/// <see cref="Entries"/> are only the tracks added in MusicHoarder, which play after the synced ones.
/// One row per source, created when the playlists are listed.</item>
/// </list>
/// Entries are song ids, so a member's playlist can hold a grantor's songs; every read re-checks them
/// through <c>ILibraryScopeResolver</c>, so a revoked grant drops them from the playlist at once.
/// </summary>
public class Playlist
{
    public int Id { get; set; }

    public Guid OwnerUserId { get; set; }

    /// <summary>
    /// The playlist's name. A source-backed playlist shows its source's current name instead, so a
    /// rename on Spotify or YouTube carries over; this column keeps the last one seen.
    /// </summary>
    [MaxLength(512)]
    public string Name { get; set; } = string.Empty;

    /// <summary>The collected remote playlist this one mirrors; null for a native playlist.</summary>
    public int? WishlistSourceId { get; set; }
    public WishlistSource? WishlistSource { get; set; }

    /// <summary>
    /// Whether the playlist is written as an <c>.m3u8</c> into the destination library's playlists
    /// folder (so Navidrome/Plex/Jellyfin import it). On by default for a native playlist, off for a
    /// source-backed one — Playlist sync already mirrors Spotify playlists, and two files of one
    /// playlist would show up twice on the media server.
    /// </summary>
    public bool ExportToLibrary { get; set; }

    /// <summary>Absolute path of the <c>.m3u8</c> last written for this playlist, if any.</summary>
    [MaxLength(2048)]
    public string? ExportFilePath { get; set; }

    public DateTime? ExportedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public List<PlaylistEntry> Entries { get; set; } = [];
}

/// <summary>
/// One song added to a <see cref="Playlist"/> in MusicHoarder. A song is on a playlist at most once.
/// </summary>
public class PlaylistEntry
{
    public int Id { get; set; }

    public int PlaylistId { get; set; }
    public Playlist Playlist { get; set; } = null!;

    /// <summary>
    /// The song — possibly another account's, for a member's playlist of songs shared with them.
    /// Resolved through the library scope on every read, never trusted on its own.
    /// </summary>
    public int SongId { get; set; }
    public SongMetadata Song { get; set; } = null!;

    /// <summary>Order within the playlist's MusicHoarder additions (ascending).</summary>
    public int Position { get; set; }

    public DateTime AddedAtUtc { get; set; }
}
