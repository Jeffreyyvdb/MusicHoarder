namespace MusicHoarder.Api.Discover;

/// <summary>One archived playlist, as the merged catalog knows it.</summary>
/// <param name="SourceBaseUrl">The archive repo that supplied this entry, so its tracklist is read from the same place.</param>
public sealed record ArchivedPlaylist(
    string Id,
    string Name,
    string? Description,
    long Followers,
    string? OwnerName,
    string SnapshotId,
    string SourceBaseUrl);

public sealed record ArchivedTrack(
    string SpotifyTrackId,
    string Title,
    IReadOnlyList<string> Artists,
    string? Album,
    int DurationMs,
    DateTime? AddedAtUtc);

public sealed record ArchivedPlaylistTracks(ArchivedPlaylist Playlist, IReadOnlyList<ArchivedTrack> Tracks);

/// <summary>
/// Spotify's own playlists (Today's Top Hits, RapCaviar, ...), read from the public Spotify Playlist Archive
/// on GitHub. Spotify's Web API refuses these to personal apps; the archive snapshots them daily.
/// </summary>
public interface ISpotifyPlaylistArchive
{
    /// <summary>The most followed playlists matching <paramref name="search"/>; null when no archive source could be read.</summary>
    Task<IReadOnlyList<ArchivedPlaylist>?> BrowseAsync(string? search, int limit, CancellationToken ct);

    /// <summary>One playlist by id; null when it is not archived, the id is malformed, or no source could be read.</summary>
    Task<ArchivedPlaylist?> FindAsync(string playlistId, CancellationToken ct);

    /// <summary>A playlist with its full tracklist; null when the archive cannot answer.</summary>
    Task<ArchivedPlaylistTracks?> GetTracksAsync(string playlistId, CancellationToken ct);

    /// <summary>The playlist's cover image URL, or null when Spotify's oEmbed does not give one.</summary>
    Task<string?> GetCoverUrlAsync(string playlistId, CancellationToken ct);
}
