using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// The kind of Spotify collection an <see cref="ExportedPlaylist"/> mirrors to an on-disk M3U file.
/// </summary>
public enum ExportedPlaylistKind
{
    LikedSongs,
    Playlist,
}

/// <summary>
/// A Spotify collection (Liked Songs or a single playlist) the export service mirrors as a static
/// <c>.m3u8</c> file in the destination library, so Navidrome/Plex/Jellyfin auto-import it. The file
/// lists the local built tracks that match the Spotify tracks, in Spotify order (Liked Songs by
/// liked-date descending). This row is the on-disk manifest: it drives the coverage UI and lets the
/// export service delete the <c>.m3u8</c> when a playlist is removed/renamed on Spotify. Owner-scoped
/// (Spotify is per-user).
/// </summary>
public class ExportedPlaylist
{
    public int Id { get; set; }

    /// <summary>Owner of this export — Spotify accounts are per-user.</summary>
    public Guid OwnerUserId { get; set; }

    public ExportedPlaylistKind Kind { get; set; }

    /// <summary>Spotify playlist id; null for <see cref="ExportedPlaylistKind.LikedSongs"/>.</summary>
    [MaxLength(64)]
    public string? SpotifyPlaylistId { get; set; }

    /// <summary>Raw Spotify collection name (display + filename source).</summary>
    [MaxLength(512)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Absolute path of the <c>.m3u8</c> file last written for this collection.</summary>
    [MaxLength(2048)]
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Total tracks the Spotify collection contained on the last run (coverage denominator).</summary>
    public int SpotifyTrackTotal { get; set; }

    /// <summary>Local built tracks written to the file on the last run (coverage numerator).</summary>
    public int MatchedTrackCount { get; set; }

    public DateTime? LastGeneratedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
