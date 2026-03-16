using System.IO.Abstractions;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Library;

public record LibraryBuildBatchResult(
    int TotalTracks,
    int Done,
    int Failed,
    TimeSpan Duration);

public interface ILibraryBuilderService
{
    Task<LibraryBuildBatchResult> ProcessNextBatchAsync(Guid runId, CancellationToken ct = default);
}

public interface ILibraryTagWriter
{
    Task WriteTagsAsync(string path, SongMetadata song, CancellationToken ct = default);
}

public class TagLibLibraryTagWriter : ILibraryTagWriter
{
    public Task WriteTagsAsync(string path, SongMetadata song, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var tagFile = TagLib.File.Create(path);
        var tag = tagFile.Tag;

        tag.Title = NullIfEmpty(song.Title);
        tag.Album = NullIfEmpty(song.Album);
        tag.Performers = BuildPerformerArray(song.Artist);
        tag.AlbumArtists = BuildAlbumArtistArray(song.AlbumArtist, song.Artist);
        tag.Year = song.Year is > 0 ? (uint)song.Year.Value : 0;
        tag.Track = song.TrackNumber is > 0 ? (uint)song.TrackNumber.Value : 0;
        tag.ISRC = NullIfEmpty(song.Isrc) ?? string.Empty;

        tagFile.Save();
        return Task.CompletedTask;
    }

    private static string[] BuildPerformerArray(string? artist)
    {
        var normalized = NullIfEmpty(artist);
        if (normalized is null)
        {
            return [];
        }

        if (normalized.Contains(';'))
        {
            return normalized
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToArray();
        }

        return [normalized];
    }

    private static string[] BuildAlbumArtistArray(string? albumArtist, string? artist)
    {
        var normalizedAlbumArtist = NullIfEmpty(albumArtist);
        if (normalizedAlbumArtist is not null)
        {
            return [normalizedAlbumArtist];
        }

        var fallbackArtist = NullIfEmpty(artist);
        return fallbackArtist is null ? [] : [fallbackArtist];
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal record LibraryBuildTrackCandidate(int SongId, string DestinationPath);

internal enum LibraryBuildOutcome
{
    Done,
    Failed,
}

public class LibraryBuilderService(
    IServiceScopeFactory scopeFactory,
    IDestinationPathResolver destinationPathResolver,
    IFileSystem fileSystem,
    ILibraryTagWriter tagWriter,
    IOptions<MusicEnricherOptions> options,
    ILogger<LibraryBuilderService> logger) : ILibraryBuilderService
{
    private const int CopyBufferSize = 1024 * 1024;
    private static long tempFileSequence;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> destinationLocks = new(StringComparer.Ordinal);

    public async Task<LibraryBuildBatchResult> ProcessNextBatchAsync(Guid runId, CancellationToken ct = default)
    {
        var startedAt = DateTime.UtcNow;
        var opts = options.Value;

        List<LibraryBuildTrackCandidate> candidates;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
            var rawCandidates = await db.Songs
                .AsNoTracking()
                .Where(s => s.DeletedAtUtc == null)
                .Where(s => s.EnrichmentStatus == EnrichmentStatus.Matched)
                .Where(s => s.LibraryBuildStatus != LibraryBuildStatus.Done
                    || s.DestinationPath == null
                    || s.PreviousDestinationPath != null)
                .OrderBy(s => s.Id)
                .Take(opts.LibraryBuilderBatchSize)
                .ToListAsync(ct);

            var uniqueDestinationPaths = new HashSet<string>(StringComparer.Ordinal);
            candidates = [];
            foreach (var candidateSong in rawCandidates)
            {
                var destinationPath = destinationPathResolver.ResolvePath(candidateSong);
                if (uniqueDestinationPaths.Add(destinationPath))
                {
                    candidates.Add(new LibraryBuildTrackCandidate(candidateSong.Id, destinationPath));
                }
                else
                {
                    logger.LogWarning(
                        "Deferring song {SongId}: destination path collision in current batch for {DestinationPath}",
                        candidateSong.Id,
                        destinationPath);
                }
            }
        }

        if (candidates.Count == 0)
        {
            return new LibraryBuildBatchResult(0, 0, 0, DateTime.UtcNow - startedAt);
        }

        logger.LogInformation("Starting library build run {RunId} with {Count} tracks", runId, candidates.Count);

        var done = 0;
        var failed = 0;
        var semaphore = new SemaphoreSlim(opts.LibraryBuilderWorkerConcurrency, opts.LibraryBuilderWorkerConcurrency);

        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = opts.LibraryBuilderWorkerConcurrency * 2,
                CancellationToken = ct
            },
            async (candidate, token) =>
            {
                await semaphore.WaitAsync(token);
                try
                {
                    LibraryBuildOutcome outcome;
                    using (await AcquireDestinationLockAsync(candidate.DestinationPath, token))
                    {
                        outcome = await ProcessTrackAsync(candidate.SongId, candidate.DestinationPath, token);
                    }

                    switch (outcome)
                    {
                        case LibraryBuildOutcome.Done:
                            Interlocked.Increment(ref done);
                            break;
                        case LibraryBuildOutcome.Failed:
                            Interlocked.Increment(ref failed);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

        var duration = DateTime.UtcNow - startedAt;
        logger.LogInformation(
            "Library build run {RunId} complete: Total={Total}, Done={Done}, Failed={Failed}, Duration={Duration:F1}s",
            runId,
            candidates.Count,
            done,
            failed,
            duration.TotalSeconds);

        return new LibraryBuildBatchResult(candidates.Count, done, failed, duration);
    }

    private async Task<LibraryBuildOutcome> ProcessTrackAsync(int songId, string destinationPath, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var song = await db.Songs.FirstOrDefaultAsync(s => s.Id == songId, ct);
        if (song is null || song.IsDeleted || song.EnrichmentStatus != EnrichmentStatus.Matched)
        {
            logger.LogDebug("Skipping song {SongId}: not buildable (missing/deleted/not-matched)", songId);
            return LibraryBuildOutcome.Failed;
        }

        var legacyPath = ResolveLegacyDestinationPath(song);
        var currentManagedPath = ResolveCurrentManagedPath(song, destinationPath, legacyPath);

        if (currentManagedPath is not null && !PathsEqual(currentManagedPath, destinationPath))
        {
            song.PreviousDestinationPath = currentManagedPath;
        }

        var destinationDirectory = fileSystem.Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            logger.LogWarning(
                "Library build failed for {Track} (SongId={SongId}): destination directory resolution returned empty. DestinationPath={DestinationPath}",
                song.TrackLabel, songId, destinationPath);
            song.MarkBuildFailed("Could not resolve destination directory");
            await db.SaveChangesAsync(ct);
            return LibraryBuildOutcome.Failed;
        }

        var tempPath = BuildTempPath(destinationPath, songId);
        song.LibraryBuildLastAttemptedAtUtc = DateTime.UtcNow;

        try
        {
            logger.LogInformation("Building {Track} (SongId={SongId}) -> {DestinationPath}",
                song.TrackLabel, songId, destinationPath);

            fileSystem.Directory.CreateDirectory(destinationDirectory);

            if (fileSystem.File.Exists(destinationPath))
            {
                var existingSize = fileSystem.FileInfo.New(destinationPath).Length;
                if (existingSize == song.FileSizeBytes)
                {
                    song.MarkBuildDone(destinationPath);
                    await db.SaveChangesAsync(ct);
                    logger.LogInformation(
                        "Skipping copy for {Track} (SongId={SongId}): destination already exists with same size ({Bytes} bytes)",
                        song.TrackLabel, songId, existingSize);
                    return LibraryBuildOutcome.Done;
                }
            }

            if (fileSystem.File.Exists(tempPath))
            {
                fileSystem.File.Delete(tempPath);
            }

            await StreamCopyAsync(song.SourcePath, tempPath, ct);
            song.MarkCopied();
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Copied {Track} (SongId={SongId}) to temp file {TempPath}",
                song.TrackLabel, songId, tempPath);

            await tagWriter.WriteTagsAsync(tempPath, song, ct);
            song.MarkTagged();
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Tagged temp file for {Track} (SongId={SongId})",
                song.TrackLabel, songId);

            if (fileSystem.File.Exists(destinationPath))
            {
                fileSystem.File.Delete(destinationPath);
            }

            fileSystem.File.Move(tempPath, destinationPath);
            if (!string.IsNullOrWhiteSpace(song.PreviousDestinationPath)
                && !PathsEqual(song.PreviousDestinationPath, destinationPath))
            {
                DeleteManagedPathAndPrune(song.PreviousDestinationPath, options.Value.DestinationDirectory);
            }

            song.MarkBuildDone(destinationPath);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Library build complete for {Track} (SongId={SongId}): {DestinationPath}",
                song.TrackLabel, songId, destinationPath);

            return LibraryBuildOutcome.Done;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            TryDeleteFileBestEffort(tempPath, songId);

            logger.LogWarning(ex,
                "Library build failed for {Track} (SongId={SongId}). Source={SourcePath}, Temp={TempPath}, Destination={DestinationPath}",
                song.TrackLabel, songId, song.SourcePath, tempPath, destinationPath);
            song.MarkBuildFailed(ex.Message);
            await db.SaveChangesAsync(ct);
            return LibraryBuildOutcome.Failed;
        }
    }

    private async Task StreamCopyAsync(string sourcePath, string tempDestinationPath, CancellationToken ct)
    {
        await using var source = fileSystem.File.Open(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        await using var destination = fileSystem.File.Open(
            tempDestinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);
        await source.CopyToAsync(destination, CopyBufferSize, ct);
        await destination.FlushAsync(ct);
    }

    private void TryDeleteFileBestEffort(string path, int songId)
    {
        try
        {
            if (!fileSystem.File.Exists(path))
            {
                return;
            }

            fileSystem.File.Delete(path);
        }
        catch (Exception cleanupEx)
        {
            logger.LogWarning(cleanupEx,
                "Cleanup failed while deleting temp file for SongId={SongId} at {Path}",
                songId, path);
        }
    }

    private string BuildTempPath(string destinationPath, int songId)
    {
        var directory = fileSystem.Path.GetDirectoryName(destinationPath);
        var fileNameWithoutExtension = fileSystem.Path.GetFileNameWithoutExtension(destinationPath);
        var extension = fileSystem.Path.GetExtension(destinationPath);
        var uniqueToken = Interlocked.Increment(ref tempFileSequence);

        var tempSuffix = $".tmp.{songId}.{uniqueToken}";
        var tempFileName = string.IsNullOrWhiteSpace(extension)
            ? $"{fileNameWithoutExtension}{tempSuffix}"
            : $"{fileNameWithoutExtension}{tempSuffix}{extension}";

        return string.IsNullOrWhiteSpace(directory)
            ? tempFileName
            : fileSystem.Path.Combine(directory, tempFileName);
    }

    private async Task<IDisposable> AcquireDestinationLockAsync(string destinationPath, CancellationToken ct)
    {
        var canonicalPath = fileSystem.Path.GetFullPath(destinationPath);
        var semaphore = destinationLocks.GetOrAdd(canonicalPath, static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct);
        return new DestinationLockReleaser(semaphore);
    }

    private sealed class DestinationLockReleaser(SemaphoreSlim semaphore) : IDisposable
    {
        private readonly SemaphoreSlim semaphore = semaphore;
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            semaphore.Release();
        }
    }

    private string? ResolveCurrentManagedPath(SongMetadata song, string desiredPath, string legacyPath)
    {
        if (!string.IsNullOrWhiteSpace(song.DestinationPath))
        {
            return song.DestinationPath;
        }

        if (fileSystem.File.Exists(desiredPath))
        {
            return desiredPath;
        }

        return fileSystem.File.Exists(legacyPath) ? legacyPath : null;
    }

    private string ResolveLegacyDestinationPath(SongMetadata song)
    {
        var artist = NormalizeSegment(song.Artist, "Unknown Artist");
        var title = NormalizeSegment(song.Title, "Unknown Title");
        var extension = NormalizeExtension(song.Extension);

        if (song.IsUnreleased)
        {
            return Path.Combine(
                options.Value.DestinationDirectory,
                artist,
                "Unreleased",
                $"{title}{extension}");
        }

        var album = NormalizeSegment(song.Album, "Unknown Album");
        var albumFolder = song.Year is > 0
            ? $"{song.Year.Value} - {album}"
            : album;
        var trackPrefix = song.TrackNumber is > 0
            ? $"{song.TrackNumber.Value:00} - "
            : string.Empty;

        return Path.Combine(
            options.Value.DestinationDirectory,
            artist,
            albumFolder,
            $"{trackPrefix}{title}{extension}");
    }

    private void DeleteManagedPathAndPrune(string path, string destinationRoot)
    {
        if (!fileSystem.File.Exists(path))
        {
            return;
        }

        fileSystem.File.Delete(path);
        PruneEmptyDirectories(fileSystem.Path.GetDirectoryName(path), destinationRoot);
    }

    private void PruneEmptyDirectories(string? startDirectory, string destinationRoot)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return;
        }

        var current = startDirectory;
        var rootFullPath = fileSystem.Path.GetFullPath(destinationRoot);
        while (!string.IsNullOrWhiteSpace(current) && IsWithinRoot(current, rootFullPath))
        {
            if (!fileSystem.Directory.Exists(current))
            {
                break;
            }

            var hasFiles = fileSystem.Directory.EnumerateFiles(current).Any();
            var hasDirectories = fileSystem.Directory.EnumerateDirectories(current).Any();
            if (hasFiles || hasDirectories)
            {
                break;
            }

            fileSystem.Directory.Delete(current);
            if (PathsEqual(current, rootFullPath))
            {
                break;
            }

            current = fileSystem.Path.GetDirectoryName(current);
        }
    }

    private bool IsWithinRoot(string path, string rootFullPath)
    {
        var fullPath = fileSystem.Path.GetFullPath(path);
        return fullPath.StartsWith(rootFullPath, StringComparison.Ordinal);
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(a, b, StringComparison.Ordinal);

    private static string NormalizeSegment(string? value, string fallback)
    {
        var sanitized = DestinationPathResolver.Sanitize(value ?? string.Empty);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = fallback;
        }

        return sanitized.Length <= 60 ? sanitized : sanitized[..60];
    }

    private static string NormalizeExtension(string? extension)
    {
        var sanitized = DestinationPathResolver.Sanitize(extension ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return string.Empty;
        }

        return sanitized.StartsWith(".", StringComparison.Ordinal)
            ? sanitized
            : $".{sanitized}";
    }
}
