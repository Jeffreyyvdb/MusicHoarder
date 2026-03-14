using Microsoft.Extensions.Logging;

namespace MusicHoarder.Api.Scanner;

public static class MockFileGenerator
{
    private static readonly string MockDirectory = Path.Combine(Path.GetTempPath(), $"musichoarder_mock_{Guid.NewGuid()}");

    public static string MockMusicDirectory => MockDirectory;

    public static void InitializeMockFileSystem()
    {
        if (!Directory.Exists(MockDirectory))
        {
            Directory.CreateDirectory(MockDirectory);
        }
    }

    public static void CleanupMockFileSystem()
    {
        try
        {
            if (Directory.Exists(MockDirectory))
            {
                Directory.Delete(MockDirectory, true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to clean up mock directory: {ex.Message}");
        }
    }

    public static void CreateMockAudioFile(
        string relativePath,
        string? artist = null,
        string? album = null,
        string? title = null,
        int? year = null,
        int? trackNumber = null)
    {
        InitializeMockFileSystem();

        var fullPath = Path.Combine(MockDirectory, relativePath.TrimStart('/', '\\'));
        var directory = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var extension = Path.GetExtension(relativePath).ToLowerInvariant();
        byte[] fileContent;

        if (extension == ".mp3")
        {
            fileContent = CreateEmptyMp3File();
        }
        else if (extension == ".flac")
        {
            fileContent = CreateEmptyFlacFile();
        }
        else
        {
            fileContent = new byte[1024];
        }

        File.WriteAllBytes(fullPath, fileContent);

        if (!string.IsNullOrEmpty(artist) || !string.IsNullOrEmpty(album) ||
            !string.IsNullOrEmpty(title) || year.HasValue || trackNumber.HasValue)
        {
            try
            {
                using var tagFile = TagLib.File.Create(fullPath);
                tagFile.Tag.AlbumArtists = new[] { artist ?? "Unknown Artist" };
                tagFile.Tag.Album = album ?? "Unknown Album";
                tagFile.Tag.Title = title ?? Path.GetFileNameWithoutExtension(relativePath);
                tagFile.Tag.Year = (uint)(year ?? 0);
                tagFile.Tag.Track = (uint)(trackNumber ?? 0);
                tagFile.Save();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to set metadata for {relativePath}: {ex.Message}");
            }
        }
    }

    public static void CreateMockAlbum(
        string artistName,
        string albumName,
        int year,
        int trackCount,
        string fileExtension = ".mp3")
    {
        for (int i = 1; i <= trackCount; i++)
        {
            var trackTitle = $"Track {i}";
            var relativePath = $"{artistName}/{albumName}/{i:D2} - {trackTitle}{fileExtension}";
            CreateMockAudioFile(
                relativePath,
                artist: artistName,
                album: albumName,
                title: trackTitle,
                year: year,
                trackNumber: i);
        }
    }

    private static byte[] CreateEmptyMp3File()
    {
        return new byte[]
        {
            0xFF, 0xFB, 0x90, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        }.Concat(new byte[1000]).ToArray();
    }

    private static byte[] CreateEmptyFlacFile()
    {
        var header = new byte[]
        {
            0x66, 0x4C, 0x61, 0x43, 0x00, 0x00, 0x00, 0x22,
            0x12, 0x00, 0x00, 0x10, 0x00, 0x00, 0x10, 0x00
        };
        return header.Concat(new byte[1000]).ToArray();
    }
}
