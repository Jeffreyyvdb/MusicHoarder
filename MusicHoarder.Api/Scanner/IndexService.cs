using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Scanner;

public record IndexProgress(
    int TotalFiles,
    int Scanned,
    int NewFiles,
    int ChangedFiles,
    int DeletedFiles,
    string CurrentFile);

public record IndexResult(
    int TotalFiles,
    int NewFiles,
    int ChangedFiles,
    int DeletedFiles,
    TimeSpan Duration);

public interface IIndexService
{
    Task<IndexResult> IndexAsync(string directoryPath,
        IProgress<IndexProgress> progress,
        CancellationToken cancellationToken = default);
}

public class IndexService(
    IFileScanner fileScanner,
    MusicHoarderDbContext dbContext,
    ILogger<IndexService> logger) : IIndexService
{
    private static readonly string[] SupportedExtensions =
        { ".mp3", ".flac", ".wav", ".m4a", ".ogg", ".wma", ".aac", ".opus", ".aiff" };

    private const int BatchSize = 500;
    private const int ProgressReportInterval = 100;

    public async Task<IndexResult> IndexAsync(string directoryPath,
        IProgress<IndexProgress> progress,
        CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;

        logger.LogInformation("Starting index of {Directory}", directoryPath);

        var existingSongs = await dbContext.Songs
            .Where(s => !s.IsDeleted)
            .Select(s => new { s.SourcePath, s.LastModifiedUtc, s.FileSizeBytes })
            .ToDictionaryAsync(s => s.SourcePath, s => new { s.LastModifiedUtc, s.FileSizeBytes }, cancellationToken);

        var supportedFiles = new List<string>();
        var filesToScan = new List<string>();

        var totalFiles = 0;
        var deletedFiles = 0;
        var newFiles = 0;
        var changedFiles = 0;

        progress?.Report(new IndexProgress(0, 0, 0, 0, 0, "Scanning directory..."));

        var existingFilePaths = new HashSet<string>();

        foreach (var file in Directory.EnumerateFiles(directoryPath, "*.*", new EnumerationOptions
                 {
                     IgnoreInaccessible = true,
                     RecurseSubdirectories = true
                 }))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!SupportedExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                continue;

            supportedFiles.Add(file);
            totalFiles++;

            var fileInfo = new FileInfo(file);
            var lastModified = fileInfo.LastWriteTimeUtc;
            var fileSize = fileInfo.Length;

            if (!existingSongs.TryGetValue(file, out var existing))
            {
                filesToScan.Add(file);
                newFiles++;
            }
            else if (!DateTimeIsEqualMicroseconds(existing.LastModifiedUtc, lastModified) || existing.FileSizeBytes != fileSize)
            {
                filesToScan.Add(file);
                changedFiles++;
            }
            else
            {
                existingFilePaths.Add(file);
            }
        }

        var deletedFilePaths = existingSongs.Keys.Except(existingFilePaths).ToList();
        deletedFiles = deletedFilePaths.Count;

        progress?.Report(new IndexProgress(totalFiles, 0, newFiles, changedFiles, deletedFiles, "Starting..."));

        if (deletedFilePaths.Count > 0)
        {
            var deletedSongs = await dbContext.Songs
                .Where(s => deletedFilePaths.Contains(s.SourcePath))
                .ToListAsync(cancellationToken);

            foreach (var song in deletedSongs)
            {
                song.DeletedAtUtc = DateTime.UtcNow;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Marked {Count} files as deleted", deletedFiles);
        }

        progress?.Report(new IndexProgress(totalFiles, 0, newFiles, changedFiles, deletedFiles,
            "Scanning metadata..."));

        var scannedMetadata = await fileScanner.ScanSpecificFilesAsync(filesToScan, cancellationToken);

        var scannedCount = scannedMetadata.Count;

        progress?.Report(new IndexProgress(totalFiles, scannedCount, newFiles, changedFiles, deletedFiles,
            "Saving to database..."));

        var existingPathsToUpdate = new HashSet<string>(scannedMetadata.Select(m => m.SourcePath));

        var existingSongsToUpdate = await dbContext.Songs
            .Where(s => existingPathsToUpdate.Contains(s.SourcePath))
            .ToDictionaryAsync(s => s.SourcePath, s => s, cancellationToken);

        var batch = new List<SongMetadata>();
        var updatedCount = 0;

        foreach (var metadata in scannedMetadata)
        {
            if (existingSongsToUpdate.TryGetValue(metadata.SourcePath, out var existingSong))
            {
                existingSong.FileSizeBytes = metadata.FileSizeBytes;
                existingSong.LastModifiedUtc = metadata.LastModifiedUtc;
                existingSong.Artist = metadata.Artist;
                existingSong.Album = metadata.Album;
                existingSong.Title = metadata.Title;
                existingSong.Year = metadata.Year;
                existingSong.TrackNumber = metadata.TrackNumber;
                existingSong.Fingerprint = metadata.Fingerprint;
                existingSong.DurationSeconds = metadata.DurationSeconds;
                existingSong.IndexedAtUtc = DateTime.UtcNow;
                existingSong.DeletedAtUtc = null;
                updatedCount++;
            }
            else
            {
                batch.Add(metadata);
            }

            if (batch.Count >= BatchSize)
            {
                dbContext.Songs.AddRange(batch);
                await dbContext.SaveChangesAsync(cancellationToken);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            dbContext.Songs.AddRange(batch);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (updatedCount > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var duration = DateTime.UtcNow - startTime;

        progress?.Report(new IndexProgress(totalFiles, scannedCount, newFiles, changedFiles, deletedFiles, "Complete"));

        logger.LogInformation(
            "Index complete: Total={Total}, New={New}, Changed={Changed}, Deleted={Deleted}, Duration={Duration}s",
            totalFiles, newFiles, changedFiles, deletedFiles, duration.TotalSeconds);

        return new IndexResult(totalFiles, newFiles, changedFiles, deletedFiles, duration);
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="a"></param>
    /// <param name="b"></param>
    /// <returns></returns>
    public static bool DateTimeIsEqualMicroseconds(DateTime a, DateTime b)
        => Math.Abs((a - b).TotalMicroseconds) < 1;
}