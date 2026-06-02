namespace MusicHoarder.Api.Artwork;

/// <summary>Raw bytes + reported mime of a picture embedded in an audio file's tags.</summary>
public sealed record EmbeddedPicture(byte[] Data, string MimeType);

/// <summary>
/// Reads embedded artwork from an audio file. Isolated behind an interface because TagLib reads
/// the real disk by path (it ignores <c>IFileSystem</c>), so callers that run on a mock filesystem
/// (e.g. the library builder tests) can substitute a fake instead of touching TagLib.
/// </summary>
public interface IEmbeddedPictureReader
{
    /// <summary>
    /// Returns the front-cover picture (or the first picture if none is tagged front-cover),
    /// or <c>null</c> when the file has no embedded art or cannot be read.
    /// </summary>
    EmbeddedPicture? ReadFront(string filePath);
}

public class TagLibEmbeddedPictureReader(ILogger<TagLibEmbeddedPictureReader> logger) : IEmbeddedPictureReader
{
    public EmbeddedPicture? ReadFront(string filePath)
    {
        try
        {
            using var tagFile = TagLib.File.Create(filePath);
            var pictures = tagFile.Tag.Pictures;
            if (pictures is null || pictures.Length == 0)
            {
                return null;
            }

            var picture = Array.Find(pictures, p => p.Type == TagLib.PictureType.FrontCover) ?? pictures[0];
            var bytes = picture.Data?.Data;
            if (bytes is null || bytes.Length == 0)
            {
                return null;
            }

            return new EmbeddedPicture(bytes, picture.MimeType ?? string.Empty);
        }
        catch (Exception ex)
        {
            logger.LogDebug("Could not read embedded picture from {File}: {Message}", filePath, ex.Message);
            return null;
        }
    }
}
