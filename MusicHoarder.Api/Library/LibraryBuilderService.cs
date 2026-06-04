using System.IO.Abstractions;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Artwork;
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
    private const string VariousArtists = "Various Artists";
    private const string VariousArtistsMbid = "89ad4ac3-39f7-470e-963a-56509c546377";

    static TagLibLibraryTagWriter()
    {
        // ID3v2.4 is required for real multi-value frames (v2.3 concatenates and loses them).
        TagLib.Id3v2.Tag.DefaultVersion = 4;
        TagLib.Id3v2.Tag.ForceDefaultVersion = true;
    }

    public Task WriteTagsAsync(string path, SongMetadata song, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var tagFile = TagLib.File.Create(path);
        var tag = tagFile.Tag;

        var compilation = song.IsCompilation;
        var albumArtists = compilation ? [VariousArtists] : BuildAlbumArtistArray(song.AlbumArtist, song.Artist);

        tag.Title = NullIfEmpty(song.Title);
        tag.Album = NullIfEmpty(song.Album);
        tag.Performers = BuildPerformerArray(song.Artist);
        // ALBUMARTIST stays the main artist only (or "Various Artists" for compilations) so albums
        // never fragment by per-track featured artist.
        tag.AlbumArtists = albumArtists;
        tag.Year = song.Year is > 0 ? (uint)song.Year.Value : 0;
        tag.Track = song.TrackNumber is > 0 ? (uint)song.TrackNumber.Value : 0;
        tag.TrackCount = song.TotalTracks is > 0 ? (uint)song.TotalTracks.Value : 0;
        tag.Disc = song.DiscNumber is > 0 ? (uint)song.DiscNumber.Value : 0;
        tag.DiscCount = song.TotalDiscs is > 0 ? (uint)song.TotalDiscs.Value : 0;
        tag.ISRC = NullIfEmpty(song.Isrc) ?? string.Empty;

        // MusicBrainz IDs — the generic Tag writes the Picard-compatible frame per format.
        // Gotcha: MusicBrainzTrackId is the field that holds the RECORDING id.
        // Only assign when present: the Xiph (FLAC) setters throw on null.
        SetIfPresent(NullIfEmpty(song.MusicBrainzId), v => tag.MusicBrainzTrackId = v);
        SetIfPresent(NullIfEmpty(song.MusicBrainzReleaseId), v => tag.MusicBrainzReleaseId = v);
        SetIfPresent(NullIfEmpty(song.MusicBrainzReleaseGroupId), v => tag.MusicBrainzReleaseGroupId = v);
        SetIfPresent(
            compilation ? VariousArtistsMbid : NullIfEmpty(song.AlbumArtistMusicBrainzId),
            v => tag.MusicBrainzReleaseArtistId = v);
        var artistIds = MusicHoarder.Api.Metadata.MultiValue.Split(song.ArtistMusicBrainzIds);
        SetIfPresent(artistIds.Length > 0 ? artistIds[0] : null, v => tag.MusicBrainzArtistId = v);

        // Embed lyrics: prefer synced LRC, fall back to plain
        tag.Lyrics = NullIfEmpty(song.SyncedLyrics) ?? NullIfEmpty(song.PlainLyrics) ?? string.Empty;

        // Multi-value / freeform fields the generic Tag doesn't expose. create:false so we only
        // touch the file's native tag (the generic sets above already created it) — never an
        // ID3 tag on a FLAC, which is non-spec and breaks some players.
        WriteExtendedTags(tagFile, song, albumArtists, compilation);

        tagFile.Save();
        return Task.CompletedTask;
    }

    private static void SetIfPresent(string? value, Action<string> set)
    {
        if (!string.IsNullOrWhiteSpace(value)) set(value);
    }

    private static void WriteExtendedTags(TagLib.File file, SongMetadata song, string[] albumArtists, bool compilation)
    {
        var artists = MusicHoarder.Api.Metadata.MultiValue.Split(song.Artists);
        if (artists.Length == 0)
        {
            artists = BuildPerformerArray(song.Artist);
        }

        var releaseTypes = MusicHoarder.Api.Metadata.MultiValue.Split(song.ReleaseTypes);

        if (file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3)
        {
            SetId3UserText(id3, "ARTISTS", artists);
            SetId3UserText(id3, "ALBUMARTISTS", albumArtists);
            SetId3UserText(id3, "MusicBrainz Album Type", releaseTypes);
            SetId3Text(id3, "TCMP", compilation ? ["1"] : []);
        }

        if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph)
        {
            SetXiph(xiph, "ARTISTS", artists);
            SetXiph(xiph, "ALBUMARTISTS", albumArtists);
            SetXiph(xiph, "RELEASETYPE", releaseTypes);
            SetXiph(xiph, "COMPILATION", compilation ? ["1"] : []);
        }

        if (file.GetTag(TagLib.TagTypes.Apple, false) is TagLib.Mpeg4.AppleTag apple)
        {
            apple.SetDashBoxes("com.apple.iTunes", "ARTISTS", artists);
            apple.SetDashBoxes("com.apple.iTunes", "ALBUMARTISTS", albumArtists);
            apple.SetDashBoxes("com.apple.iTunes", "MusicBrainz Album Type", releaseTypes);
            apple.IsCompilation = compilation;
        }
    }

    private static void SetId3UserText(TagLib.Id3v2.Tag id3, string description, string[] values)
    {
        if (values.Length == 0)
        {
            var existing = TagLib.Id3v2.UserTextInformationFrame.Get(id3, description, false);
            if (existing is not null) id3.RemoveFrame(existing);
            return;
        }

        TagLib.Id3v2.UserTextInformationFrame.Get(id3, description, true).Text = values;
    }

    private static void SetId3Text(TagLib.Id3v2.Tag id3, TagLib.ByteVector frameId, string[] values)
    {
        if (values.Length == 0)
        {
            var existing = TagLib.Id3v2.TextInformationFrame.Get(id3, frameId, false);
            if (existing is not null) id3.RemoveFrame(existing);
            return;
        }

        TagLib.Id3v2.TextInformationFrame.Get(id3, frameId, true).Text = values;
    }

    private static void SetXiph(TagLib.Ogg.XiphComment xiph, string key, string[] values)
    {
        if (values.Length == 0)
        {
            xiph.RemoveField(key);
            return;
        }

        xiph.SetField(key, values);
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

// Carries what the post-batch album-cover pass needs from a successful build: the source file to
// resolve art from and whether the track is unreleased (those folders mix unrelated singles, so we
// don't drop a single shared cover into them).
internal sealed record LibraryBuildTrackResult(
    LibraryBuildOutcome Outcome,
    string? SourcePath = null,
    bool IsUnreleased = false);

public class LibraryBuilderService(
    IServiceScopeFactory scopeFactory,
    IDestinationPathResolver destinationPathResolver,
    IFileSystem fileSystem,
    ILibraryDestinationCleaner destinationCleaner,
    ILibraryTagWriter tagWriter,
    IAlbumCoverWriter albumCoverWriter,
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
            // Background service: bypass the per-user query filter. Skip synthetic (demo) rows —
            // they have no real source file to copy and are pre-seeded as Done.
            var rawCandidates = await db.Songs
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic)
                .Where(s => !s.IsDuplicate)
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

        // Destination album folder -> a representative source file to lift the cover from. Populated
        // by successful, non-unreleased builds; drained once after the track loop so each folder gets
        // a single cover.* write with no intra-album races.
        var coverDirectories = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions
            {
                // Match the worker concurrency (the gating semaphore) rather than 2×: the surplus
                // tasks only pinned more thread-pool threads on synchronous TagLib tag writes,
                // starving request handling under load.
                MaxDegreeOfParallelism = opts.LibraryBuilderWorkerConcurrency,
                CancellationToken = ct
            },
            async (candidate, token) =>
            {
                await semaphore.WaitAsync(token);
                try
                {
                    LibraryBuildTrackResult result;
                    using (await AcquireDestinationLockAsync(candidate.DestinationPath, token))
                    {
                        result = await ProcessTrackAsync(candidate.SongId, candidate.DestinationPath, token);
                    }

                    switch (result.Outcome)
                    {
                        case LibraryBuildOutcome.Done:
                            Interlocked.Increment(ref done);
                            if (!result.IsUnreleased && !string.IsNullOrEmpty(result.SourcePath))
                            {
                                var directory = fileSystem.Path.GetDirectoryName(candidate.DestinationPath);
                                if (!string.IsNullOrEmpty(directory))
                                {
                                    coverDirectories.TryAdd(directory, result.SourcePath);
                                }
                            }
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

        await WriteAlbumCoversAsync(coverDirectories, opts.LibraryBuilderWorkerConcurrency, ct);

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

    private async Task<LibraryBuildTrackResult> ProcessTrackAsync(int songId, string destinationPath, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var song = await db.Songs.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == songId, ct);
        if (song is null || song.IsDeleted || song.EnrichmentStatus != EnrichmentStatus.Matched)
        {
            logger.LogDebug("Skipping song {SongId}: not buildable (missing/deleted/not-matched)", songId);
            return new LibraryBuildTrackResult(LibraryBuildOutcome.Failed);
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
            return new LibraryBuildTrackResult(LibraryBuildOutcome.Failed);
        }

        var tempPath = BuildTempPath(destinationPath, songId);
        song.LibraryBuildLastAttemptedAtUtc = DateTime.UtcNow;

        try
        {
            logger.LogInformation("Building {Track} (SongId={SongId}) -> {DestinationPath}",
                song.TrackLabel, songId, destinationPath);

            fileSystem.Directory.CreateDirectory(destinationDirectory);

            // Skip the copy when a same-size file already sits at the destination — but only on a
            // fresh build. A forced rebuild (signalled by PreviousDestinationPath, set by
            // ResetLibraryBuild) must always re-copy + re-tag so changed metadata reaches the file;
            // the size heuristic compares against the source and could otherwise drop the re-tag.
            if (string.IsNullOrWhiteSpace(song.PreviousDestinationPath) && fileSystem.File.Exists(destinationPath))
            {
                var existingSize = fileSystem.FileInfo.New(destinationPath).Length;
                if (existingSize == song.FileSizeBytes)
                {
                    song.MarkBuildDone(destinationPath);
                    await db.SaveChangesAsync(ct);
                    logger.LogInformation(
                        "Skipping copy for {Track} (SongId={SongId}): destination already exists with same size ({Bytes} bytes)",
                        song.TrackLabel, songId, existingSize);
                    return new LibraryBuildTrackResult(LibraryBuildOutcome.Done, song.SourcePath, song.IsUnreleased);
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
                destinationCleaner.DeleteManagedPathAndPrune(song.PreviousDestinationPath, options.Value.DestinationDirectory);
            }

            song.MarkBuildDone(destinationPath);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Library build complete for {Track} (SongId={SongId}): {DestinationPath}",
                song.TrackLabel, songId, destinationPath);

            return new LibraryBuildTrackResult(LibraryBuildOutcome.Done, song.SourcePath, song.IsUnreleased);
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
            return new LibraryBuildTrackResult(LibraryBuildOutcome.Failed);
        }
    }

    // Writes a cover.<ext> into each freshly-built album folder that doesn't already have a
    // cover/folder/front.* image, lifting art from a representative source track (folder image first,
    // else embedded — Navidrome's order). One task per directory, so no intra-album races. Best-effort:
    // a cover failure never fails the build.
    private async Task WriteAlbumCoversAsync(
        ConcurrentDictionary<string, string> directories,
        int maxDegreeOfParallelism,
        CancellationToken ct)
    {
        if (directories.IsEmpty)
        {
            return;
        }

        await Parallel.ForEachAsync(
            directories,
            new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism, CancellationToken = ct },
            (entry, _) =>
            {
                albumCoverWriter.WriteIfMissing(entry.Key, entry.Value);
                return ValueTask.CompletedTask;
            });
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
