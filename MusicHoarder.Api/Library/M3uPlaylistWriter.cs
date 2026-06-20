using System.Text;

namespace MusicHoarder.Api.Library;

/// <summary>One track line in an exported playlist: the built file plus the tags for its #EXTINF.</summary>
public sealed record M3uEntry(string DestinationPath, string? Artist, string? Title, int? DurationSeconds);

public interface IM3uPlaylistWriter
{
    /// <summary>
    /// Atomically writes an Extended-M3U file at <paramref name="filePath"/>. Track paths are written
    /// relative to <paramref name="playlistsDir"/> (the file's own folder) with forward slashes, so the
    /// file is portable across servers. Creates the directory if needed.
    /// </summary>
    Task WriteAsync(string filePath, string playlistsDir, IReadOnlyList<M3uEntry> entries, CancellationToken ct = default);
}

public sealed class M3uPlaylistWriter : IM3uPlaylistWriter
{
    // No BOM: Navidrome/Plex/Jellyfin all read UTF-8 M3U and a BOM is not required (and trips some
    // stricter parsers). The .m3u8 extension already signals UTF-8.
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public async Task WriteAsync(string filePath, string playlistsDir, IReadOnlyList<M3uEntry> entries, CancellationToken ct = default)
    {
        Directory.CreateDirectory(playlistsDir);
        var content = BuildContent(playlistsDir, entries);

        // Write to a temp file in the same directory then atomically rename, so a server scanning the
        // folder never sees a half-written playlist.
        var tempPath = filePath + ".tmp";
        await File.WriteAllTextAsync(tempPath, content, Utf8NoBom, ct);
        File.Move(tempPath, filePath, overwrite: true);
    }

    /// <summary>Builds the Extended-M3U text (testable without touching disk).</summary>
    public static string BuildContent(string playlistsDir, IReadOnlyList<M3uEntry> entries)
    {
        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n");
        foreach (var entry in entries)
        {
            var seconds = entry.DurationSeconds is > 0 ? entry.DurationSeconds.Value : -1;
            var artist = (entry.Artist ?? string.Empty).Trim();
            var title = (entry.Title ?? string.Empty).Trim();
            sb.Append($"#EXTINF:{seconds},{artist} - {title}\n");
            sb.Append(BuildRelativePath(playlistsDir, entry.DestinationPath));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Path of <paramref name="destinationPath"/> relative to the playlist folder, forward-slashed.</summary>
    public static string BuildRelativePath(string playlistsDir, string destinationPath)
        => Path.GetRelativePath(playlistsDir, destinationPath).Replace('\\', '/');
}
