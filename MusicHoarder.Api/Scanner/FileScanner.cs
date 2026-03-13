using System.Collections.Concurrent;
using System.Diagnostics;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Scanner;

public interface IFileScanner
{
    Task<List<SongMetadata>> ScanFilesAsync(
        string directoryPath,
        CancellationToken ct = default);
    Task<List<SongMetadata>> ScanSpecificFilesAsync(
        List<string> filePaths,
        CancellationToken ct = default);
}

public class FileScanner(ILogger<FileScanner> logger) : IFileScanner
{
    private static readonly HashSet<string> SupportedExtensions =
    [
        ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".wav", ".wma", ".opus", ".aiff"
    ];

    public async Task<List<SongMetadata>> ScanFilesAsync(
        string directoryPath,
        CancellationToken ct = default)
    {
        var files = Directory.EnumerateFiles(directoryPath, "*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true
            })
            .Where(f => SupportedExtensions.Contains(
                Path.GetExtension(f).ToLowerInvariant()));

        var results = new ConcurrentBag<SongMetadata>();

        var scanned = 0;
        var extracted = 0;
        var startTime = Stopwatch.GetTimestamp();

        // Rolling window: track count and time at last log point
        var lastLoggedScanned = 0;
        var lastLoggedTime = Stopwatch.GetTimestamp();

        await Parallel.ForEachAsync(files, new ParallelOptions()
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = ct
        }, (filePath, tokena) =>
        {
            var metadata = TryExtractMetadata(filePath);
            var currentScanned = Interlocked.Increment(ref scanned);

            if (metadata is not null)
            {
                results.Add(metadata);
                Interlocked.Increment(ref extracted);
            }


            // Log every 10 files to avoid log spam
            if (currentScanned % 10 != 0) return ValueTask.CompletedTask;

            var now = Stopwatch.GetTimestamp();

            // Files processed since last log
            var filesInWindow = currentScanned - lastLoggedScanned;
            var secondsInWindow = Stopwatch.GetElapsedTime(lastLoggedTime, now).TotalSeconds;
            var perSecond = filesInWindow / secondsInWindow;

            // Update last log point
            lastLoggedScanned = currentScanned;
            lastLoggedTime = now;

            logger.LogInformation(
                "{Extracted} songs extracted ({PerSecond:F1}/s)"
                , extracted, perSecond);

            return ValueTask.CompletedTask;
        });

        return results.ToList();
    }

    public async Task<List<SongMetadata>> ScanSpecificFilesAsync(
        List<string> filePaths,
        CancellationToken ct = default)
    {
        var results = new ConcurrentBag<SongMetadata>();

        var scanned = 0;
        var extracted = 0;
        var startTime = Stopwatch.GetTimestamp();

        var lastLoggedScanned = 0;
        var lastLoggedTime = Stopwatch.GetTimestamp();

        await Parallel.ForEachAsync(filePaths, new ParallelOptions()
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = ct
        }, (filePath, tokena) =>
        {
            var metadata = TryExtractMetadata(filePath);
            var currentScanned = Interlocked.Increment(ref scanned);

            if (metadata is not null)
            {
                results.Add(metadata);
                Interlocked.Increment(ref extracted);
            }

            if (currentScanned % 10 != 0) return ValueTask.CompletedTask;

            var now = Stopwatch.GetTimestamp();

            var filesInWindow = currentScanned - lastLoggedScanned;
            var secondsInWindow = Stopwatch.GetElapsedTime(lastLoggedTime, now).TotalSeconds;
            var perSecond = filesInWindow / secondsInWindow;

            lastLoggedScanned = currentScanned;
            lastLoggedTime = now;

            logger.LogInformation(
                "{Extracted} songs extracted ({PerSecond:F1}/s)"
                , extracted, perSecond);

            return ValueTask.CompletedTask;
        });

        return results.ToList();
    }

    private SongMetadata? TryExtractMetadata(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        var extension = Path.GetExtension(filePath).ToLowerInvariant();

        try
        {
            var fileInfo = new FileInfo(filePath);
            var fileSize = fileInfo.Length;
            var lastModified = fileInfo.LastWriteTimeUtc;

            string? artist = null;
            string? album = null;
            string? title = null;
            int? year = null;
            int? trackNumber = null;

            try
            {
                using var tagFile = TagLib.File.Create(filePath);
                var tag = tagFile.Tag;

                album = NullIfEmpty(tag.Album);
                artist = tag.AlbumArtists?.Length > 0
                    ? NullIfEmpty(tag.AlbumArtists[0])
                    : NullIfEmpty(tag.FirstPerformer);
                title = NullIfEmpty(tag.Title);
                year = tag.Year != 0 ? (int)tag.Year : null;
                trackNumber = tag.Track != 0 ? (int)tag.Track : null;
            }
            catch (Exception ex)
            {
                logger.LogDebug("Could not read tags from {File}: {Message}", fileName, ex.Message);
                // Continue — we still return the file with basic metadata
            }

            return new SongMetadata
            {
                FilePath = filePath,
                FileName = fileName,
                Extension = extension,
                FileSize = fileSize,
                LastModified = lastModified,
                Artist = artist,
                Album = album,
                Title = title,
                Year = year,
                TrackNumber = trackNumber,
                IndexedAt = DateTime.UtcNow,
                IsDeleted = false,
                DeletedAt = null
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to process {File}: {Message}", fileName, ex.Message);
            return null;
        }
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}