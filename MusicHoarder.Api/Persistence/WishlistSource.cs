using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// The kind of Spotify collection a <see cref="WishlistSource"/> tracks.
/// </summary>
public enum WishlistSourceType
{
    LikedSongs,
    Playlist,
}

/// <summary>
/// A Spotify collection (Liked Songs or a single playlist) the owner chose to "collect": its tracks
/// are snapshotted into <see cref="WishlistItem"/>s and, when <see cref="AutoSync"/> is on, the
/// background sync keeps appending newly-liked / newly-added tracks. Owner-scoped (Spotify is per-user).
/// </summary>
public class WishlistSource
{
    public int Id { get; set; }

    /// <summary>Owner of this source — Spotify accounts are per-user.</summary>
    public Guid OwnerUserId { get; set; }

    public WishlistSourceType SourceType { get; set; }

    /// <summary>Spotify playlist id; null for <see cref="WishlistSourceType.LikedSongs"/>.</summary>
    [MaxLength(64)]
    public string? SpotifyPlaylistId { get; set; }

    [MaxLength(512)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(1024)]
    public string? ImageUrl { get; set; }

    /// <summary>When true the sync service keeps adding newly liked / newly added tracks to the wishlist.</summary>
    public bool AutoSync { get; set; }

    public DateTime? LastSyncedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
