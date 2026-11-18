using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// The kind of remote collection a <see cref="WishlistSource"/> tracks. Appended only: the values are
/// stored as integers.
/// </summary>
public enum WishlistSourceType
{
    LikedSongs,
    Playlist,

    /// <summary>A Deezer editorial/discover playlist (keyed by <see cref="WishlistSource.DeezerPlaylistId"/>).</summary>
    DeezerPlaylist,

    /// <summary>
    /// A YouTube playlist (keyed by <see cref="WishlistSource.YouTubePlaylistId"/>). Its items download
    /// the exact video on the list, audio and music video both, rather than searching by name.
    /// </summary>
    YouTubePlaylist,

    /// <summary>
    /// One of Spotify's own playlists, read from the public Spotify Playlist Archive (keyed by
    /// <see cref="WishlistSource.SpotifyPlaylistId"/>). Needs no Spotify account, so it keeps syncing
    /// while Spotify is disconnected.
    /// </summary>
    SpotifyArchivePlaylist,
}

/// <summary>
/// A remote collection (Spotify Liked Songs or playlist, a Deezer playlist, a YouTube playlist) the owner chose to "collect": its tracks
/// are snapshotted into <see cref="WishlistItem"/>s and, when <see cref="AutoSync"/> is on, the
/// background sync keeps appending newly-liked / newly-added tracks. Owner-scoped (Spotify is per-user).
/// </summary>
public class WishlistSource
{
    public int Id { get; set; }

    /// <summary>Owner of this source — Spotify accounts are per-user.</summary>
    public Guid OwnerUserId { get; set; }

    public WishlistSourceType SourceType { get; set; }

    /// <summary>Spotify playlist id; set for <see cref="WishlistSourceType.Playlist"/> and <see cref="WishlistSourceType.SpotifyArchivePlaylist"/> sources.</summary>
    [MaxLength(64)]
    public string? SpotifyPlaylistId { get; set; }

    /// <summary>Deezer playlist id; set only for <see cref="WishlistSourceType.DeezerPlaylist"/> sources.</summary>
    [MaxLength(64)]
    public string? DeezerPlaylistId { get; set; }

    /// <summary>YouTube playlist id (the <c>list=</c> value); set only for <see cref="WishlistSourceType.YouTubePlaylist"/> sources.</summary>
    [MaxLength(64)]
    public string? YouTubePlaylistId { get; set; }

    /// <summary>
    /// The remote tracklist's fingerprint from the last complete read: Deezer's playlist checksum, or an
    /// archived Spotify playlist's snapshot id. When it matches the current one the sync skips the read
    /// (nothing changed).
    /// </summary>
    [MaxLength(128)]
    public string? RemoteChecksum { get; set; }

    [MaxLength(512)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(1024)]
    public string? ImageUrl { get; set; }

    /// <summary>When true the sync service keeps adding newly liked / newly added tracks to the wishlist.</summary>
    public bool AutoSync { get; set; }

    public DateTime? LastSyncedAtUtc { get; set; }

    /// <summary>
    /// When the sync last recorded the remote list's full tracklist as <see cref="WishlistSourceTrack"/>
    /// rows. Null for a source not completely read since those rows existed — its playlist then falls
    /// back to the items this source introduced, and a Deezer sync does not skip an unchanged checksum.
    /// </summary>
    public DateTime? TracksRecordedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// True for the sources read through the owner's Spotify account. Deezer and YouTube playlists and
    /// archived Spotify playlists are read from public listings, so they keep syncing while Spotify is not connected.
    /// </summary>
    public bool NeedsSpotify => SourceType is WishlistSourceType.LikedSongs or WishlistSourceType.Playlist;
}
