using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Audio;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Soulseek;

/// <summary>
/// Completes quality upgrades: once the pipeline has scanned + fingerprinted a downloaded candidate
/// (its provisional row exists at <see cref="UpgradeRequest.DownloadedFilePath"/> with a
/// fingerprint), verify against REAL file facts — advertised bitrates lie — and swap it into the
/// target row via <see cref="SongMetadata.ApplySourceUpgrade"/>: the target keeps its Id,
/// enrichment, and lyrics; only the file identity and fingerprint change. The provisional row is
/// hard-deleted (it's transient, pre-enrichment — and the (OwnerUserId, SourcePath) unique index
/// forbids two rows on the handed-over path). The re-queued build then swaps the destination file,
/// and the post-build sync hook propagates the upgrade to the remote instance.
/// </summary>
public class UpgradeMergeService(
    MusicHoarderDbContext db,
    JobManager jobManager,
    IOwnerLookupService ownerLookup,
    IOptionsMonitor<SlskdOptions> slskdOptions,
    IOptions<MusicEnricherOptions> enricherOptions,
    ILogger<UpgradeMergeService> logger)
{
    /// <summary>Processes every AwaitingIngest request whose provisional row is ready. Returns how
    /// many requests reached a terminal state this pass.</summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        // IgnoreQueryFilters: background scope → the tenant filter resolves to Guid.Empty and would
        // hide the owner's rows. Owner scoping is explicit below.
        var pending = await db.UpgradeRequests
            .IgnoreQueryFilters()
            .Include(r => r.Song)
            .Where(r => r.OwnerUserId == ownerLookup.OwnerUserId
                && r.Status == UpgradeRequestStatus.AwaitingIngest)
            .OrderBy(r => r.Id)
            .ToListAsync(ct);

        var terminal = 0;
        foreach (var request in pending)
        {
            ct.ThrowIfCancellationRequested();
            if (await TryMergeAsync(request, ct))
                terminal++;
        }
        return terminal;
    }

    /// <summary>True when the request reached a terminal state (merged or failed); false = not ready yet.</summary>
    internal async Task<bool> TryMergeAsync(UpgradeRequest request, CancellationToken ct)
    {
        var target = request.Song;
        var downloadedPath = request.DownloadedFilePath;
        if (target is null || string.IsNullOrEmpty(downloadedPath))
        {
            request.MarkTerminal(UpgradeRequestStatus.Failed, "request lost its song or downloaded file path");
            await db.SaveChangesAsync(ct);
            return true;
        }

        var provisional = await db.Songs
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.OwnerUserId == request.OwnerUserId
                && s.SourcePath == downloadedPath && s.DeletedAtUtc == null, ct);

        if (provisional is null)
        {
            // Not scanned yet — unless the file itself vanished, in which case the request is dead.
            if (!File.Exists(downloadedPath))
            {
                request.MarkTerminal(UpgradeRequestStatus.Failed, "downloaded file disappeared before ingest");
                await db.SaveChangesAsync(ct);
                return true;
            }
            return false;
        }

        if (string.IsNullOrEmpty(provisional.Fingerprint))
            return false; // fingerprint stage hasn't run yet

        if (target.IsDeleted)
        {
            await AbortMergeAsync(request, provisional, downloadedPath, "target song was deleted mid-upgrade", ct);
            return true;
        }

        // Verify with real file facts. Duration must agree (same recording), quality must actually
        // improve — a peer advertising 900 kbps FLAC that fingerprints as a 96 kbps transcode fails here.
        if (!DurationsAgree(provisional, target))
        {
            await AbortMergeAsync(request, provisional, downloadedPath,
                $"duration mismatch (downloaded {provisional.DurationSeconds}s vs target {target.DurationSeconds}s)", ct);
            return true;
        }

        // Same-recording check: a peer can send a different track of similar length, which the duration
        // gate alone can't catch. Compare the acoustic fingerprints (the downloaded file's, freshly
        // computed by the pipeline, vs the target's). Fail-OPEN: a missing/undecodable fingerprint or a
        // disabled threshold skips the gate rather than blocking a legitimate upgrade on a tooling gap.
        var minSimilarity = enricherOptions.Value.QualityUpgradeFingerprintMinSimilarity;
        if (minSimilarity > 0)
        {
            var similarity = Audio.ChromaprintComparer.Similarity(provisional.Fingerprint, target.Fingerprint);
            if (similarity is { } sim && sim < minSimilarity)
            {
                await AbortMergeAsync(request, provisional, downloadedPath,
                    $"downloaded file is a different recording (fingerprint similarity {sim:F3} < {minSimilarity:F3})", ct);
                return true;
            }
            if (similarity is { } ok)
                logger.LogInformation("Upgrade fingerprint check passed for song {SongId}: similarity {Similarity:F3}",
                    target.Id, ok);
        }

        var provisionalScore = AudioQuality.Score(provisional);
        var targetScore = AudioQuality.Score(target);
        if (provisionalScore <= targetScore
            || AudioQuality.TierFor(provisional.Extension) < AudioQuality.TierFor(target.Extension))
        {
            await AbortMergeAsync(request, provisional, downloadedPath,
                $"downloaded file is not actually better (score {provisionalScore} vs {targetScore})", ct);
            return true;
        }

        // Capture facts, then free the SourcePath by deleting the provisional row FIRST (separate
        // SaveChanges — EF's statement ordering must not race the unique index). A crash between the
        // two saves just re-creates the provisional on the next scan and the merge re-runs.
        var facts = new
        {
            provisional.SourcePath,
            provisional.FileSizeBytes,
            provisional.FileName,
            provisional.Extension,
            provisional.LastModifiedUtc,
            provisional.Bitrate,
            provisional.Fingerprint,
            provisional.DurationSeconds,
            provisional.DurationMs,
        };
        // If the pipeline raced us and already built the provisional (fingerprint → enrich → build
        // can complete between sweeps), its destination copy would be orphaned by the row delete.
        DeleteOrphanedDestination(provisional.DestinationPath);
        db.Songs.Remove(provisional);
        await db.SaveChangesAsync(ct);

        var oldSourcePath = target.SourcePath;
        target.ApplySourceUpgrade(
            facts.SourcePath, facts.FileSizeBytes, facts.FileName, facts.Extension,
            facts.LastModifiedUtc, facts.Bitrate, facts.Fingerprint,
            facts.DurationSeconds, facts.DurationMs);
        // The duplicate flags referenced the old fingerprint pairing; the next dedup run recomputes.
        if (target.IsDuplicate)
            target.ClearDuplicate();
        target.ResetLibraryBuild();
        request.MarkTerminal(UpgradeRequestStatus.Completed);
        await db.SaveChangesAsync(ct);

        DeleteOldSourceFile(oldSourcePath);

        if (jobManager.TryStartJob(JobType.Build, out var jobId, out _))
            logger.LogInformation("Upgrade merge triggered build {JobId}", jobId);
        logger.LogInformation(
            "Upgrade complete for song {SongId} '{Artist} - {Title}': {OldExt} score {OldScore} → {NewExt} score {NewScore}",
            target.Id, LogSanitizer.ForLog(target.Artist), LogSanitizer.ForLog(target.Title),
            Path.GetExtension(oldSourcePath), targetScore, facts.Extension, provisionalScore);
        return true;
    }

    private async Task AbortMergeAsync(
        UpgradeRequest request, SongMetadata provisional, string downloadedPath, string reason, CancellationToken ct)
    {
        DeleteOrphanedDestination(provisional.DestinationPath);
        db.Songs.Remove(provisional);
        request.MarkTerminal(UpgradeRequestStatus.Failed, reason);
        await db.SaveChangesAsync(ct);
        try
        {
            if (File.Exists(downloadedPath))
                File.Delete(downloadedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to delete rejected upgrade download {Path}", LogSanitizer.ForLog(downloadedPath));
        }
        logger.LogInformation("Upgrade for song {SongId} aborted: {Reason}", request.SongId, reason);
    }

    private void DeleteOrphanedDestination(string? destinationPath)
    {
        if (string.IsNullOrEmpty(destinationPath))
            return;
        try
        {
            if (File.Exists(destinationPath))
                File.Delete(destinationPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to delete provisional row's destination file {Path}",
                LogSanitizer.ForLog(destinationPath));
        }
    }

    private static bool DurationsAgree(SongMetadata a, SongMetadata b)
    {
        // Unknown durations can't veto; when both sides know, they must agree within tolerance.
        var aSec = a.DurationSeconds ?? (a.DurationMs is { } ams ? ams / 1000 : (int?)null);
        var bSec = b.DurationSeconds ?? (b.DurationMs is { } bms ? bms / 1000 : (int?)null);
        if (aSec is null || bSec is null)
            return true;
        return Math.Abs(aSec.Value - bSec.Value) <= 10;
    }

    /// <summary>
    /// The replaced source file is deleted only when it lives under a writable managed directory
    /// (the download staging dir or the synced-source dir). Files in the read-only source library
    /// are left in place — the scanner will re-index them as a new row (accepted v1 behavior; the
    /// builder's position guard prevents a destination collision).
    /// </summary>
    private void DeleteOldSourceFile(string sourcePath)
    {
        var managedRoots = new[]
        {
            enricherOptions.Value.DownloadDirectory,
            slskdOptions.CurrentValue.DownloadsDirectory,
        };
        var normalized = sourcePath.Replace('\\', '/');
        var isManaged = managedRoots.Any(root => !string.IsNullOrWhiteSpace(root)
            && normalized.StartsWith(root.Replace('\\', '/').TrimEnd('/') + "/", StringComparison.Ordinal));
        if (!isManaged)
        {
            logger.LogInformation(
                "Old source file left in place (read-only source library): {Path} — it will re-index as a new row",
                LogSanitizer.ForLog(sourcePath));
            return;
        }

        try
        {
            if (File.Exists(sourcePath))
                File.Delete(sourcePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to delete replaced source file {Path}", LogSanitizer.ForLog(sourcePath));
        }
    }
}
