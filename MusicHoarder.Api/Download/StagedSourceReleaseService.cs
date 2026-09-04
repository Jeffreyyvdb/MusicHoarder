using System.IO.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Scanner;

namespace MusicHoarder.Api.Download;

/// <summary>Dry-run view: what a release would touch. Pure DB arithmetic, reads no files.</summary>
public sealed record StagedSourceReleasePreview(
    int Eligible,
    long EligibleBytes,
    int Released,
    long ReleasedBytes,
    int GraceMinutes,
    string? UnavailableReason);

/// <summary>What one release run did. <see cref="IdleReason"/> is set when it could not run at all.</summary>
public sealed record StagedSourceReleaseResult(
    int Candidates,
    int Released,
    int AlreadyMissing,
    int SkippedVerification,
    int Raced,
    int Failed,
    long BytesReclaimed,
    string? IdleReason = null)
{
    /// <summary>The wishlist downloader is off, so there is no staging directory to release from.</summary>
    public const string IdleDownloadsDisabled = "downloads-disabled";

    /// <summary>No <c>MusicEnricher:DownloadDirectory</c> is configured.</summary>
    public const string IdleNoDownloadDirectory = "no-download-directory";

    /// <summary>The download and destination roots nest, so "under staging" and "in the library" overlap.</summary>
    public const string IdleRootsNested = "roots-nested";

    public static StagedSourceReleaseResult Idle(string reason) => new(0, 0, 0, 0, 0, 0, 0, reason);
}

/// <summary>
/// Deletes the staged copy of a download once its library copy has been verified, so downloads stop
/// being stored twice. A download is indexed from <see cref="MusicEnricherOptions.DownloadDirectory"/>
/// as the song's source and copied into the library by the builder; from then on the destination is
/// the copy everything reads (streaming, sync, tagging), and the staged file is dead weight that, left
/// alone, fills the disk.
/// <para>
/// Ordering is what makes this safe against the scanner: the row is stamped
/// <see cref="SongMetadata.SourceReleasedAtUtc"/> and saved <b>before</b> the file is unlinked, and the
/// scanner skips stamped rows in deletion reconciliation — so a scan landing mid-run cannot read the
/// missing file as a deletion. A stamp is reverted if the delete fails or the row was reset underneath
/// us, so a stamped row always means "the destination is the only copy, on purpose".
/// </para>
/// </summary>
public class StagedSourceReleaseService(
    MusicHoarderDbContext db,
    IFileSystem fileSystem,
    IAudioFileProbe probe,
    IOptions<MusicEnricherOptions> options,
    IOptionsMonitor<SyncOptions> syncOptions,
    StagedSourceReleaseTracker tracker,
    ILogger<StagedSourceReleaseService> logger)
{
    /// <summary>
    /// Re-tagging rewrites only the tag block; the audio bytes are identical. A destination this much
    /// smaller than the source is not a re-tag but a truncated or foreign file — leave the source alone.
    /// </summary>
    internal const double MinDestinationSizeRatio = 0.9;

    /// <summary>Container durations differ by a frame or two across writers; a real mismatch is seconds.</summary>
    internal const int DurationToleranceMs = 3000;

    private sealed record Candidate(
        int Id, string SourcePath, string DestinationPath, long FileSizeBytes, int? DurationMs, int? DurationSeconds);

    /// <summary>Why the release cannot run at all, or null when it can.</summary>
    public string? UnavailableReason()
    {
        var opts = options.Value;
        if (!opts.EnableWishlistDownloads)
            return StagedSourceReleaseResult.IdleDownloadsDisabled;
        if (string.IsNullOrWhiteSpace(opts.DownloadDirectory))
            return StagedSourceReleaseResult.IdleNoDownloadDirectory;
        if (SongOriginResolver.IsUnder(opts.DestinationDirectory, opts.DownloadDirectory)
            || SongOriginResolver.IsUnder(opts.DownloadDirectory, opts.DestinationDirectory)
            || PathsEqual(opts.DestinationDirectory, opts.DownloadDirectory))
            return StagedSourceReleaseResult.IdleRootsNested;
        return null;
    }

    public async Task<StagedSourceReleasePreview> PreviewAsync(Guid ownerId, CancellationToken ct = default)
    {
        var grace = options.Value.StagedSourceReleaseGraceMinutes;
        var reason = UnavailableReason();
        if (reason is not null)
            return new StagedSourceReleasePreview(0, 0, 0, 0, grace, reason);

        var eligible = await Eligible(ownerId, DateTime.UtcNow.AddMinutes(-grace))
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Bytes = g.Sum(s => s.FileSizeBytes) })
            .FirstOrDefaultAsync(ct);

        var downloadPrefix = IndexService.NormalizeRootPrefix(options.Value.DownloadDirectory);
        var released = await db.Songs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.OwnerUserId == ownerId && s.DeletedAtUtc == null
                && s.SourceReleasedAtUtc != null && s.SourcePath.StartsWith(downloadPrefix))
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Bytes = g.Sum(s => s.FileSizeBytes) })
            .FirstOrDefaultAsync(ct);

        return new StagedSourceReleasePreview(
            eligible?.Count ?? 0, eligible?.Bytes ?? 0,
            released?.Count ?? 0, released?.Bytes ?? 0,
            grace, null);
    }

    /// <summary>
    /// Releases every eligible row's staged source. Pages by id so rows that fail verification (and
    /// therefore stay eligible) cannot make the loop spin; reports progress to the tracker per row.
    /// </summary>
    public async Task<StagedSourceReleaseResult> ReleaseAsync(Guid ownerId, CancellationToken ct = default)
    {
        var reason = UnavailableReason();
        if (reason is not null)
        {
            logger.LogInformation("Staged-source release idle: {Reason}", reason);
            return StagedSourceReleaseResult.Idle(reason);
        }

        var opts = options.Value;
        var cutoff = DateTime.UtcNow.AddMinutes(-opts.StagedSourceReleaseGraceMinutes);
        var downloadRoot = opts.DownloadDirectory;
        var destinationRoot = opts.DestinationDirectory;

        var candidates = await Eligible(ownerId, cutoff).CountAsync(ct);
        tracker.SetCandidates(candidates);
        logger.LogInformation("Staged-source release: {Count} candidate rows past the {Grace}-minute grace",
            candidates, opts.StagedSourceReleaseGraceMinutes);

        int released = 0, alreadyMissing = 0, skipped = 0, raced = 0, failed = 0;
        long bytes = 0;
        var lastId = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var page = await Eligible(ownerId, cutoff)
                .Where(s => s.Id > lastId)
                .OrderBy(s => s.Id)
                .Take(opts.StagedSourceReleaseBatchSize)
                .Select(s => new Candidate(
                    s.Id, s.SourcePath, s.DestinationPath!, s.FileSizeBytes, s.DurationMs, s.DurationSeconds))
                .ToListAsync(ct);
            if (page.Count == 0)
                break;

            foreach (var c in page)
            {
                ct.ThrowIfCancellationRequested();
                lastId = c.Id;

                // Boundary-safe guards on both paths: the eligibility query matched on a prefix, this
                // is the last line before a delete and must never be talked into touching a file the
                // builder does not manage or a source outside staging.
                if (!SongOriginResolver.IsUnder(c.SourcePath, downloadRoot)
                    || !SongOriginResolver.IsUnder(c.DestinationPath, destinationRoot))
                {
                    failed++;
                    logger.LogWarning(
                        "Refusing to release song {SongId}: paths are not under the managed roots (Source={Source}, Destination={Destination})",
                        c.Id, c.SourcePath, c.DestinationPath);
                    Report();
                    continue;
                }

                var verification = VerifyDestination(c);
                if (verification is not null)
                {
                    skipped++;
                    logger.LogInformation("Skipping release of song {SongId}: {Reason} ({Destination})",
                        c.Id, verification, c.DestinationPath);
                    Report();
                    continue;
                }

                var outcome = await ReleaseOneAsync(c, downloadRoot, ct);
                switch (outcome)
                {
                    case ReleaseOutcome.Released:
                        released++;
                        bytes += c.FileSizeBytes;
                        break;
                    case ReleaseOutcome.AlreadyMissing:
                        alreadyMissing++;
                        break;
                    case ReleaseOutcome.Raced:
                        raced++;
                        break;
                    case ReleaseOutcome.Failed:
                        failed++;
                        break;
                }
                Report();
            }
        }

        logger.LogInformation(
            "Staged-source release complete: released {Released} ({Bytes} bytes), {Missing} already missing, {Skipped} skipped verification, {Raced} raced, {Failed} failed",
            released, bytes, alreadyMissing, skipped, raced, failed);
        return new StagedSourceReleaseResult(candidates, released, alreadyMissing, skipped, raced, failed, bytes);

        void Report() => tracker.Report(released, alreadyMissing, skipped, raced, failed, bytes);
    }

    private enum ReleaseOutcome { Released, AlreadyMissing, Raced, Failed }

    private async Task<ReleaseOutcome> ReleaseOneAsync(Candidate c, string downloadRoot, CancellationToken ct)
    {
        // 1. Stamp the row first. From this moment the scanner treats the missing file as intended.
        var song = await db.Songs.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == c.Id, ct);
        if (song is null || !StillEligible(song, c))
        {
            db.ChangeTracker.Clear();
            return ReleaseOutcome.Raced;
        }

        song.MarkSourceReleased();
        await db.SaveChangesAsync(ct);

        // 2. Re-read what is actually in the database now. A build/re-tag/upgrade that ran between our
        //    load and our save changed the row's build state; the stamp must not stand on such a row.
        var current = await db.Songs.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.Id == c.Id)
            .Select(s => new { s.LibraryBuildStatus, s.PreviousDestinationPath, s.SourcePath, s.DeletedAtUtc })
            .FirstAsync(ct);
        if (current.LibraryBuildStatus != LibraryBuildStatus.Done
            || current.PreviousDestinationPath is not null
            || current.DeletedAtUtc is not null
            || !string.Equals(current.SourcePath, c.SourcePath, StringComparison.Ordinal))
        {
            await RevertAsync(song, ct);
            logger.LogInformation("Song {SongId} changed while being released; stamp reverted", c.Id);
            return ReleaseOutcome.Raced;
        }

        // 3. Unlink. A source that is already gone (hand-deleted) keeps the stamp — that is exactly the
        //    state the stamp describes, and it saves the row from the next scan's deletion sweep.
        if (!fileSystem.File.Exists(c.SourcePath))
        {
            db.ChangeTracker.Clear();
            return ReleaseOutcome.AlreadyMissing;
        }

        try
        {
            fileSystem.File.Delete(c.SourcePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await RevertAsync(song, ct);
            logger.LogWarning(ex, "Could not delete staged source for song {SongId} at {Path}; stamp reverted",
                c.Id, c.SourcePath);
            return ReleaseOutcome.Failed;
        }

        PruneEmptyParents(fileSystem.Path.GetDirectoryName(c.SourcePath), downloadRoot);
        db.ChangeTracker.Clear();
        return ReleaseOutcome.Released;
    }

    private async Task RevertAsync(SongMetadata song, CancellationToken ct)
    {
        song.ClearSourceRelease();
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    private static bool StillEligible(SongMetadata song, Candidate c) =>
        song.LibraryBuildStatus == LibraryBuildStatus.Done
        && song.DestinationPath is not null
        && song.PreviousDestinationPath is null
        && !song.IsSourceReleased
        && song.DeletedAtUtc is null
        && string.Equals(song.SourcePath, c.SourcePath, StringComparison.Ordinal);

    /// <summary>Returns why the destination copy cannot be trusted, or null when it checks out.</summary>
    private string? VerifyDestination(Candidate c)
    {
        if (!fileSystem.File.Exists(c.DestinationPath))
            return "destination file is missing";

        if (c.FileSizeBytes > 0)
        {
            var length = fileSystem.FileInfo.New(c.DestinationPath).Length;
            if (length < c.FileSizeBytes * MinDestinationSizeRatio)
                return $"destination is {length} bytes, source was {c.FileSizeBytes}";
        }

        var probed = probe.Probe(c.DestinationPath);
        if (probed is not { DurationMs: > 0 } result)
            return "destination is not readable as audio";

        var expectedMs = c.DurationMs ?? (c.DurationSeconds is { } sec ? sec * 1000 : (int?)null);
        if (expectedMs is > 0 && Math.Abs(result.DurationMs - expectedMs.Value) > DurationToleranceMs)
            return $"destination duration {result.DurationMs} ms differs from expected {expectedMs} ms";

        return null;
    }

    /// <summary>
    /// Removes now-empty folders left behind under staging, stopping <b>before</b> the staging root
    /// itself (the downloader expects it to exist; in a container it is the mount point).
    /// </summary>
    private void PruneEmptyParents(string? directory, string downloadRoot)
    {
        var rootFull = fileSystem.Path.GetFullPath(downloadRoot);
        var current = directory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var currentFull = fileSystem.Path.GetFullPath(current);
            if (PathsEqual(currentFull, rootFull) || !SongOriginResolver.IsUnder(currentFull, rootFull))
                break;

            try
            {
                if (!fileSystem.Directory.Exists(currentFull)
                    || fileSystem.Directory.EnumerateFileSystemEntries(currentFull).Any())
                    break;
                fileSystem.Directory.Delete(currentFull);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not empty after all (mount eventual consistency) or busy: leave it for next time.
                break;
            }

            current = fileSystem.Path.GetDirectoryName(currentFull);
        }
    }

    private IQueryable<SongMetadata> Eligible(Guid ownerId, DateTime cutoff)
    {
        var downloadPrefix = IndexService.NormalizeRootPrefix(options.Value.DownloadDirectory);
        var sync = syncOptions.CurrentValue;
        var syncedPrefix = sync.Mode == SyncMode.Receive && !string.IsNullOrWhiteSpace(sync.SyncedSourceDirectory)
            ? IndexService.NormalizeRootPrefix(sync.SyncedSourceDirectory)
            : null;

        var query = db.Songs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .ExcludingDemoTenant()
            .Where(s => s.OwnerUserId == ownerId
                && s.DeletedAtUtc == null
                && !s.IsSynthetic
                && s.SourceReleasedAtUtc == null
                && s.LibraryBuildStatus == LibraryBuildStatus.Done
                && s.DestinationPath != null
                && s.PreviousDestinationPath == null
                && s.DestinationPath != s.SourcePath
                && s.SourcePath.StartsWith(downloadPrefix)
                // LibraryBuiltAtUtc survives an in-place re-tag (RequeueForRetag keeps it), so the
                // last-written stamp is what actually proves the destination has settled.
                && s.LibraryBuiltAtUtc != null && s.LibraryBuiltAtUtc < cutoff
                && (s.LastWrittenAtUtc == null || s.LastWrittenAtUtc < cutoff));

        // A receiving instance may point its synced root inside the download root; those files belong
        // to the sync feature (byte-repair keys on them) and are not this feature's to release.
        if (syncedPrefix is not null)
            query = query.Where(s => !s.SourcePath.StartsWith(syncedPrefix));

        return query;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a.TrimEnd('/', '\\'), b.TrimEnd('/', '\\'), StringComparison.Ordinal);
}
