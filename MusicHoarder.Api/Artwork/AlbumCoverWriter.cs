using System.IO.Abstractions;

namespace MusicHoarder.Api.Artwork;

/// <summary>
/// Writes a destination album folder's <c>cover.&lt;ext&gt;</c> from a representative source track,
/// so Navidrome (pointed at the destination) shows folder-level art. Shared by the library builder
/// (post-batch pass) and the one-time backfill so the skip/extension/best-effort rules live in one place.
/// </summary>
public interface IAlbumCoverWriter
{
    /// <summary>
    /// Writes <c>cover.&lt;ext&gt;</c> into <paramref name="destinationDirectory"/> — resolving the art
    /// from <paramref name="sourceAudioPath"/> (folder image first, then embedded) — unless the folder
    /// already has a <c>cover/folder/front.*</c> image. Best-effort: failures are logged, not thrown.
    /// Returns <c>true</c> if a cover was written.
    /// </summary>
    bool WriteIfMissing(string destinationDirectory, string sourceAudioPath);
}

public class AlbumCoverWriter(
    IFileSystem fileSystem,
    ICoverArtResolver coverArtResolver,
    ILogger<AlbumCoverWriter> logger) : IAlbumCoverWriter
{
    public bool WriteIfMissing(string destinationDirectory, string sourceAudioPath)
    {
        try
        {
            if (!fileSystem.Directory.Exists(destinationDirectory)
                || coverArtResolver.DirectoryHasCoverImage(destinationDirectory))
            {
                return false;
            }

            var cover = coverArtResolver.Resolve(sourceAudioPath);
            if (cover is null)
            {
                return false;
            }

            var bytes = cover.Bytes
                ?? (cover.FilePath is not null ? fileSystem.File.ReadAllBytes(cover.FilePath) : null);
            if (bytes is null || bytes.Length == 0)
            {
                return false;
            }

            var coverPath = fileSystem.Path.Combine(
                destinationDirectory,
                $"cover{CoverArtResolver.ExtensionForContentType(cover.ContentType)}");
            fileSystem.File.WriteAllBytes(coverPath, bytes);
            logger.LogInformation("Wrote album cover {CoverPath}", coverPath);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write album cover in {Directory}", destinationDirectory);
            return false;
        }
    }
}
