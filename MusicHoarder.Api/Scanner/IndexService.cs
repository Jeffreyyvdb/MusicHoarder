using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Scanner;

public record IndexResult(
    int TotalFiles,
    int NewFiles,
    int ChangedFiles,
    int DeletedFiles,
    int SkippedFiles,
    int FailedFiles,
    TimeSpan Duration);

public interface IIndexService
{
    Task<IndexResult> IndexAsync(
        Guid scanId,
        string directoryPath,
        CancellationToken cancellationToken = default);
}

public class IndexService(
    IFileScanner fileScanner,
    MusicHoarderDbContext dbContext,
    ScanProgressTracker progressTracker,
    IOptions<MusicEnricherOptions> options,
    ILogger<IndexService> logger) : IIndexService
{
    private static readonly HashSet<string> SupportedExtensions =
    [
        ".mp3", ".flac", ".wav", ".m4a", ".ogg", ".wma", ".aac", ".opus", ".aiff"
    ];

    public async Task<IndexResult> IndexAsync(
        Guid scanId,
        string directoryPath,
        CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        var opts = options.Value;

        logger.LogInformation("Starting index {ScanId} of {Directory}", scanId, directoryPath);

        // ── Phase 1: Load DB state for change detection ──────────────────────
        var existingSongs = await dbContext.Songs
            .Where(s => !s.DeletedAtUtc.HasValue)
            .Select(s => new { s.SourcePath, s.LastModifiedUtc, s.FileSizeBytes })
            .ToDictionaryAsync(s => s.SourcePath, cancellationToken);

        // ── Phase 2: Discover files and classify ─────────────────────────────
        var allDiscoveredPaths = new HashSet<string>(StringComparer.Ordinal);
        var newFilePaths = new HashSet<string>(StringComparer.Ordinal);
        var filesToProcess = new List<string>();

        logger.LogInformation("dEnumerating {Directory}…", directoryPath);

        foreach (var file in Directory.EnumerateFiles(directoryPath, "*.*", new EnumerationOptions
                 {
                     IgnoreInaccessible = true,
                     RecurseSubdirectories = true
                 }))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!SupportedExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                continue;

            allDiscoveredPaths.Add(file);

            if (!existingSongs.TryGetValue(file, out var existing))
            {
                filesToProcess.Add(file);
                newFilePaths.Add(file);
            }
            else
            {
                var fileInfo = new FileInfo(file);
                if (!DateTimeIsEqualMicroseconds(existing.LastModifiedUtc, fileInfo.LastWriteTimeUtc)
                    || existing.FileSizeBytes != fileInfo.Length)
                {
                    filesToProcess.Add(file);
                }
            }
        }

        var totalDiscovered = allDiscoveredPaths.Count;
        var newCount = newFilePaths.Count;
        var changedCount = filesToProcess.Count - newCount;
        var skippedCount = totalDiscovered - filesToProcess.Count;

        logger.LogInformation(
            "Discovery complete: {Total} total, {New} new, {Changed} changed, {Skipped} unchanged",
            totalDiscovered, newCount, changedCount, skippedCount);

        // ── Phase 3: Soft-delete files no longer on disk ──────────────────────
        var deletedCount = await MarkDeletedAsync(existingSongs.Keys, allDiscoveredPaths, cancellationToken);

        progressTracker.Start(scanId, totalDiscovered);
        progressTracker.AddSkipped(skippedCount);

        if (filesToProcess.Count == 0)
        {
            progressTracker.Complete(scanId);
            return new IndexResult(totalDiscovered, newCount, changedCount, deletedCount, skippedCount, 0,
                DateTime.UtcNow - startTime);
        }

        // ── Phase 4: Channel pipeline ─────────────────────────────────────────
        //   Stage A: feed file paths into a bounded channel
        //   Stage B: process (tags + fpcalc) concurrently, bounded by SemaphoreSlim
        //   Stage C: batch-write processed records to DB (single consumer)

        var processedChannel = Channel.CreateBounded<SongMetadata>(new BoundedChannelOptions(opts.DbBatchSize * 2)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        var semaphore = new SemaphoreSlim(opts.SmbConcurrency, opts.SmbConcurrency);
        var failedCount = 0;

        var processingTask = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(
                    filesToProcess,
                    new ParallelOptions
                    {
                        // Use 2× the SMB concurrency so that we always have work
                        // queued for the semaphore slots.
                        MaxDegreeOfParallelism = opts.SmbConcurrency * 2,
                        CancellationToken = cancellationToken
                    },
                    async (filePath, ct) =>
                    {
                        await semaphore.WaitAsync(ct);
                        try
                        {
                            var metadata = await fileScanner.ScanFileAsync(filePath, tagsOnly: true, ct);

                            if (metadata is not null)
                            {
                                await processedChannel.Writer.WriteAsync(metadata, ct);

                                if (newFilePaths.Contains(filePath))
                                    progressTracker.IncrementNew();
                                else
                                    progressTracker.IncrementChanged();
                            }
                            else
                            {
                                Interlocked.Increment(ref failedCount);
                                progressTracker.IncrementFailed();
                                logger.LogWarning("Skipping {File}: scanner returned null", Path.GetFileName(filePath));
                            }

                            var processed = progressTracker.GetCurrent()?.Processed ?? 0;
                            if (processed % 100 == 0)
                            {
                                logger.LogInformation(
                                    "Progress {ScanId}: {Processed}/{Total} processed",
                                    scanId, processed, filesToProcess.Count);
                            }
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    });
            }
            finally
            {
                processedChannel.Writer.Complete();
            }
        }, cancellationToken);

        var dbWriteTask = WriteToDbAsync(processedChannel.Reader, opts.DbBatchSize, cancellationToken);

        await Task.WhenAll(processingTask, dbWriteTask);

        progressTracker.Complete(scanId);

        var duration = DateTime.UtcNow - startTime;
        logger.LogInformation(
            "Index {ScanId} complete: Total={Total}, New={New}, Changed={Changed}, Deleted={Deleted}, Skipped={Skipped}, Failed={Failed}, Duration={Duration:F1}s",
            scanId, totalDiscovered, newCount, changedCount, deletedCount, skippedCount, failedCount, duration.TotalSeconds);

        return new IndexResult(totalDiscovered, newCount, changedCount, deletedCount, skippedCount, failedCount, duration);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<int> MarkDeletedAsync(
        IEnumerable<string> existingPaths,
        HashSet<string> discoveredPaths,
        CancellationToken ct)
    {
        var deletedPaths = existingPaths.Where(p => !discoveredPaths.Contains(p)).ToList();
        if (deletedPaths.Count == 0) return 0;

        var deletedSongs = await dbContext.Songs
            .Where(s => deletedPaths.Contains(s.SourcePath))
            .ToListAsync(ct);

        foreach (var song in deletedSongs)
            song.SoftDelete();

        await dbContext.SaveChangesAsync(ct);
        logger.LogInformation("Marked {Count} files as deleted", deletedSongs.Count);
        return deletedSongs.Count;
    }

    private async Task WriteToDbAsync(
        ChannelReader<SongMetadata> reader,
        int batchSize,
        CancellationToken ct)
    {
        var batch = new List<SongMetadata>(batchSize);

        await foreach (var metadata in reader.ReadAllAsync(ct))
        {
            batch.Add(metadata);
            if (batch.Count >= batchSize)
            {
                await FlushBatchAsync(batch, ct);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
            await FlushBatchAsync(batch, ct);
    }

    private async Task FlushBatchAsync(List<SongMetadata> batch, CancellationToken ct)
    {
        var paths = new HashSet<string>(batch.Select(m => m.SourcePath), StringComparer.Ordinal);

        var existingByPath = await dbContext.Songs
            .Where(s => paths.Contains(s.SourcePath))
            .ToDictionaryAsync(s => s.SourcePath, ct);

        foreach (var metadata in batch)
        {
            if (existingByPath.TryGetValue(metadata.SourcePath, out var existing))
            {
                existing.FileSizeBytes = metadata.FileSizeBytes;
                existing.LastModifiedUtc = metadata.LastModifiedUtc;
                existing.Artist = metadata.Artist;
                existing.AlbumArtist = metadata.AlbumArtist;
                existing.Album = metadata.Album;
                existing.Title = metadata.Title;
                existing.Year = metadata.Year;
                existing.TrackNumber = metadata.TrackNumber;
                existing.DurationSeconds = metadata.DurationSeconds;
                existing.DurationMs = metadata.DurationMs;
                existing.Fingerprint = metadata.Fingerprint;
                existing.IndexedAtUtc = metadata.IndexedAtUtc;
                existing.DeletedAtUtc = null;

                // File content changed — clear stale downstream state so fingerprint
                // and enrichment re-run for this track.
                existing.ResetEnrichment(restoreOriginal: false);
                existing.ResetLibraryBuild();
            }
            else
            {
                dbContext.Songs.Add(metadata);
            }
        }

        await dbContext.SaveChangesAsync(ct);
    }

    internal static bool DateTimeIsEqualMicroseconds(DateTime a, DateTime b)
        => Math.Abs((a - b).TotalMicroseconds) < 1;
}
