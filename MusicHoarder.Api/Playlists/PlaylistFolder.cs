using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Playlists;

/// <summary>
/// The destination library's playlists folder, which two writers share: Playlist sync (Spotify
/// collections, <c>PlaylistExportService</c>) and MusicHoarder's own playlists
/// (<see cref="LibraryPlaylistExporter"/>). Each keeps its file paths in its own table and steers
/// clear of the other's names; <see cref="Lock"/> keeps their runs from interleaving, so two
/// playlists of one name can never both claim the same file.
/// </summary>
public static class PlaylistFolder
{
    public static readonly SemaphoreSlim Lock = new(1, 1);

    /// <summary>The folder, or null when no destination directory is configured.</summary>
    public static string? Resolve(MusicEnricherOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.DestinationDirectory))
            return null;
        var folderName = string.IsNullOrWhiteSpace(options.PlaylistsFolderName) ? "Playlists" : options.PlaylistsFolderName;
        return Path.Combine(options.DestinationDirectory, folderName);
    }

    /// <summary>A playlist name as a file name stem: sanitized for any filesystem, at most 100 characters.</summary>
    public static string FileStem(string? name, string fallback)
    {
        var stem = DestinationPathResolver.Sanitize(name ?? string.Empty).Trim();
        if (stem.Length > 100)
            stem = stem[..100].Trim();
        return stem.Length == 0 ? fallback : stem;
    }

    /// <summary>
    /// Deletes a playlist file, but only one inside <paramref name="playlistsDir"/> — a stale absolute
    /// path must never lead out of the managed folder.
    /// </summary>
    public static bool TryDelete(string? filePath, string? playlistsDir, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(playlistsDir))
            return false;
        try
        {
            var full = Path.GetFullPath(filePath);
            var dir = Path.GetFullPath(playlistsDir);
            if (!full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return false;
            if (!File.Exists(full))
                return false;
            File.Delete(full);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete playlist file {Path}", filePath);
            return false;
        }
    }
}
