using System.IO.Abstractions;

namespace MusicHoarder.Api.Artwork;

/// <summary>
/// A resolved album cover. Exactly one of <see cref="FilePath"/> (a local image file on disk) or
/// <see cref="Bytes"/> (an embedded picture extracted into memory) is populated.
/// </summary>
public sealed record ResolvedCover
{
    public string? FilePath { get; init; }
    public byte[]? Bytes { get; init; }
    public required string ContentType { get; init; }
}

public interface ICoverArtResolver
{
    /// <summary>
    /// Resolves cover art for an audio file in Navidrome's album priority order: a local
    /// <c>cover/folder/front.*</c> image in the file's directory first, then the embedded picture.
    /// Returns <c>null</c> when neither exists.
    /// </summary>
    ResolvedCover? Resolve(string audioFilePath);

    /// <summary>True when <paramref name="directory"/> contains a <c>cover/folder/front.*</c> image.</summary>
    bool DirectoryHasCoverImage(string? directory);
}

public class CoverArtResolver(IFileSystem fileSystem, IEmbeddedPictureReader embeddedReader) : ICoverArtResolver
{
    // Navidrome's default album cover-art priority: local image files first (these names, in order),
    // then the embedded picture. Matched case-insensitively.
    private static readonly string[] CoverBaseNames = ["cover", "folder", "front"];
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"];

    public ResolvedCover? Resolve(string audioFilePath)
    {
        var directory = fileSystem.Path.GetDirectoryName(audioFilePath);
        var folderImage = FindCoverImage(directory);
        if (folderImage is not null)
        {
            return new ResolvedCover
            {
                FilePath = folderImage,
                ContentType = ContentTypeForExtension(fileSystem.Path.GetExtension(folderImage)),
            };
        }

        var embedded = embeddedReader.ReadFront(audioFilePath);
        if (embedded is not null)
        {
            return new ResolvedCover
            {
                Bytes = embedded.Data,
                ContentType = NormalizeImageMime(embedded.MimeType, embedded.Data),
            };
        }

        return null;
    }

    public bool DirectoryHasCoverImage(string? directory) => FindCoverImage(directory) is not null;

    private string? FindCoverImage(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !fileSystem.Directory.Exists(directory))
        {
            return null;
        }

        // List the directory once into a case-insensitive name lookup, then probe by priority —
        // case-insensitive matching covers "Cover.JPG", "Folder.png", etc. the way Navidrome does.
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var path in fileSystem.Directory.EnumerateFiles(directory))
            {
                byName.TryAdd(fileSystem.Path.GetFileName(path), path);
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var baseName in CoverBaseNames)
        {
            foreach (var ext in ImageExtensions)
            {
                if (byName.TryGetValue(baseName + ext, out var match))
                {
                    return match;
                }
            }
        }

        return null;
    }

    public static string ContentTypeForExtension(string? extension) => extension?.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        _ => "application/octet-stream",
    };

    /// <summary>Maps a content-type back to the canonical file extension used for the written cover.</summary>
    public static string ExtensionForContentType(string? contentType) => contentType?.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "image/bmp" => ".bmp",
        _ => ".jpg",
    };

    // Some taggers store an empty or bogus picture mime; trust it only when it looks like an image,
    // otherwise sniff the magic bytes and fall back to JPEG.
    private static string NormalizeImageMime(string? mime, byte[] data)
    {
        if (!string.IsNullOrWhiteSpace(mime) && mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return mime;
        }

        return SniffImageMime(data) ?? "image/jpeg";
    }

    internal static string? SniffImageMime(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (data.Length >= 4 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            return "image/png";
        }

        if (data.Length >= 12 && data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F'
            && data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P')
        {
            return "image/webp";
        }

        if (data.Length >= 6 && data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F')
        {
            return "image/gif";
        }

        if (data.Length >= 2 && data[0] == (byte)'B' && data[1] == (byte)'M')
        {
            return "image/bmp";
        }

        return null;
    }
}
