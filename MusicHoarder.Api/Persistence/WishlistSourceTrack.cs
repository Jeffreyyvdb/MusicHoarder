namespace MusicHoarder.Api.Persistence;

/// <summary>
/// One track of a <see cref="WishlistSource"/>, in the remote playlist's order: "what is on this
/// playlist right now". Wishlist items cannot answer that — they are deduplicated across every source
/// (a track on two playlists is one item, credited to whichever synced first) and never removed when a
/// track leaves the remote list. The sync rewrites a source's rows after each complete read of the
/// remote list, and a source-backed <see cref="Playlist"/> plays them.
/// </summary>
public class WishlistSourceTrack
{
    public int Id { get; set; }

    public int WishlistSourceId { get; set; }
    public WishlistSource WishlistSource { get; set; } = null!;

    /// <summary>Zero-based position on the remote list (Spotify's own order; Liked Songs newest first).</summary>
    public int Position { get; set; }

    /// <summary>The wishlist item the track resolved to — its song, once it is in the library.</summary>
    public int WishlistItemId { get; set; }
    public WishlistItem WishlistItem { get; set; } = null!;
}
