using MusicHoarder.Api.Discover;

namespace MusicHoarder.Api.Tests.Discover;

public sealed class FakeSpotifyPlaylistArchive : ISpotifyPlaylistArchive
{
    public Dictionary<string, ArchivedPlaylistTracks> Playlists { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Covers { get; } = new(StringComparer.Ordinal);

    public static ArchivedPlaylist Playlist(string id, string name = "Today's Top Hits", string snapshotId = "snap-1", long followers = 1) =>
        new(id, name, null, followers, "Spotify", snapshotId, "https://example.test/archive");

    public static ArchivedTrack Track(string id, string title, params string[] artists) =>
        new(id, title, artists.Length == 0 ? ["Artist"] : artists, "Album", 200_000, new DateTime(2026, 11, 6, 5, 2, 41, DateTimeKind.Utc));

    public FakeSpotifyPlaylistArchive With(ArchivedPlaylist playlist, params ArchivedTrack[] tracks)
    {
        Playlists[playlist.Id] = new ArchivedPlaylistTracks(playlist, tracks);
        return this;
    }

    public Task<IReadOnlyList<ArchivedPlaylist>?> BrowseAsync(string? search, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ArchivedPlaylist>?>(Playlists.Values.Select(p => p.Playlist).Take(limit).ToList());

    public Task<ArchivedPlaylist?> FindAsync(string playlistId, CancellationToken ct) =>
        Task.FromResult(Playlists.GetValueOrDefault(playlistId)?.Playlist);

    public Task<ArchivedPlaylistTracks?> GetTracksAsync(string playlistId, CancellationToken ct) =>
        Task.FromResult(Playlists.GetValueOrDefault(playlistId));

    public Task<string?> GetCoverUrlAsync(string playlistId, CancellationToken ct) =>
        Task.FromResult(Covers.GetValueOrDefault(playlistId));
}
