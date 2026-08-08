using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Library;

/// <summary>How a track's file got here.</summary>
public enum SongOriginKind
{
    /// <summary>Found in the source library by a scan — it was already on disk.</summary>
    Scanned,

    /// <summary>MusicHoarder fetched it into the download staging directory (wishlist, URL add, quality upgrade).</summary>
    Downloaded,

    /// <summary>Pushed here by another MusicHoarder instance's sync.</summary>
    Synced,
}

/// <summary>
/// The collection that put a track on the wishlist — the "why" behind a download. Also set for a
/// track that was already owned when the wishlist saw it (<see cref="WishlistItemStatus.SkippedOwned"/>),
/// which is what lets the library answer "which of these did I like on Spotify, and when".
/// </summary>
public enum SongOriginSource
{
    None,
    SpotifyLiked,
    SpotifyPlaylist,
    DeezerPlaylist,
    DirectUrl,

    /// <summary>
    /// Queued by album completion because the owner already held another track from the album — nobody
    /// asked for it directly. Display only: the authoritative "is this mine" fact is
    /// <see cref="SongMetadata.AcquisitionIntent"/>, because this one is derived from a link that can
    /// legitimately disappear (soft-delete, upgrade merge, wishlist deletion).
    /// </summary>
    AlbumCompletion,
}

/// <summary>
/// Where a track came from, as shown in the library's Source column.
/// <paramref name="Detail"/> names the specific collection (a playlist name, a URL host) when there is
/// one. <paramref name="SpotifyAddedAtUtc"/> is Spotify's own added/liked timestamp — the date the user
/// saved the track on Spotify, which is unrelated to the local <see cref="SongMetadata.LikedAtUtc"/>.
/// </summary>
public readonly record struct SongOrigin(
    SongOriginKind Kind,
    SongOriginSource Source,
    string? Detail,
    DateTime? SpotifyAddedAtUtc);

/// <summary>
/// The wishlist facts about one song, flattened from <see cref="WishlistItem"/> + its
/// <see cref="WishlistSource"/> so the resolver stays free of EF types.
/// </summary>
public readonly record struct WishlistLink(
    WishlistSourceType? SourceType,
    string? SourceName,
    string? SourceUrl,
    DateTime? SpotifyAddedAtUtc,
    WishlistItemOrigin Origin = WishlistItemOrigin.UserRequested,
    string? Album = null);

/// <summary>
/// Derives a track's provenance. There is no stored origin column: the file's location says how it
/// arrived (each ingest path writes into its own root), and the wishlist link says why. Deriving beats
/// stamping a column because it stays correct for rows that predate any of these features.
/// </summary>
public static class SongOriginResolver
{
    public static SongOrigin Resolve(
        string sourcePath,
        WishlistLink? link,
        string? downloadDirectory,
        string? syncedSourceDirectory)
    {
        var kind = ResolveKind(sourcePath, downloadDirectory, syncedSourceDirectory);
        if (link is not { } l) return new SongOrigin(kind, SongOriginSource.None, null, null);

        var (source, detail) = l.SourceType switch
        {
            WishlistSourceType.LikedSongs => (SongOriginSource.SpotifyLiked, l.SourceName ?? "Liked Songs"),
            WishlistSourceType.Playlist => (SongOriginSource.SpotifyPlaylist, l.SourceName),
            WishlistSourceType.DeezerPlaylist => (SongOriginSource.DeezerPlaylist, l.SourceName),
            // Album completion carries no WishlistSource (there's no remote collection to sync), so it
            // is identified by the item's own origin discriminator instead.
            null when l.Origin == WishlistItemOrigin.AlbumCompletion => (SongOriginSource.AlbumCompletion, l.Album),
            // No wishlist source — a one-off "add from URL", where the link IS the provenance.
            _ => (SongOriginSource.DirectUrl, HostOf(l.SourceUrl)),
        };

        return new SongOrigin(kind, source, detail, l.SpotifyAddedAtUtc);
    }

    private static SongOriginKind ResolveKind(string sourcePath, string? downloadDirectory, string? syncedSourceDirectory)
    {
        // Sync is checked first: a receiving instance can point its synced root inside the download
        // root, and "arrived from my other instance" is the more specific answer.
        if (IsUnder(sourcePath, syncedSourceDirectory)) return SongOriginKind.Synced;
        if (IsUnder(sourcePath, downloadDirectory)) return SongOriginKind.Downloaded;
        return SongOriginKind.Scanned;
    }

    /// <summary>Path-prefix test on a directory boundary, so "/music-old" never matches root "/music".</summary>
    internal static bool IsUnder(string path, string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        var prefix = root.TrimEnd('/', '\\');
        return prefix.Length > 0
            && (path.StartsWith(prefix + "/", StringComparison.Ordinal)
                || path.StartsWith(prefix + "\\", StringComparison.Ordinal));
    }

    private static string? HostOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host
            : null;
    }

    /// <summary>
    /// Picks the link that best describes a song when several wishlist items point at it (a track can
    /// sit in Liked Songs and a playlist at once). Liked Songs wins — its timestamp is the real
    /// "when did I like this" — then playlists, then a bare URL; ties go to the earliest save.
    /// Album completion ranks last of all: if anything asked for this track by name, that is the more
    /// interesting answer to "where did this come from".
    /// </summary>
    public static WishlistLink Best(IEnumerable<WishlistLink> links) =>
        links
            .OrderBy(l => l.Origin == WishlistItemOrigin.AlbumCompletion ? 4 : l.SourceType switch
            {
                WishlistSourceType.LikedSongs => 0,
                WishlistSourceType.Playlist => 1,
                WishlistSourceType.DeezerPlaylist => 2,
                _ => 3,
            })
            .ThenBy(l => l.SpotifyAddedAtUtc ?? DateTime.MaxValue)
            .First();
}
