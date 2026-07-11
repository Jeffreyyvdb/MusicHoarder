using Microsoft.Extensions.Options;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Soulseek;

/// <summary>
/// Shared slskd search + transfer mechanics used by both the wishlist download provider and the
/// quality-upgrade service: run a network search to completion, then fetch one elected candidate —
/// enqueue, poll the transfer, locate the completed file in slskd's staging directory, and move it
/// into the pipeline's download staging dir under a unique stem.
/// </summary>
public class SlskdFileFetcher(
    ISlskdClient client,
    IOptionsMonitor<SlskdOptions> options,
    ILogger<SlskdFileFetcher> logger)
{
    /// <summary>Starts a search and polls until slskd reports it complete (or our ceiling passes).</summary>
    public async Task<IReadOnlyList<SlskdSearchResponse>> SearchAsync(string query, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        var search = await client.StartSearchAsync(query, ct);
        if (search is null)
            return [];

        // slskd finishes the search on its own timeout; our deadline just guards a stuck poll.
        var deadline = DateTime.UtcNow.AddSeconds(opts.SearchTimeoutSeconds + 15);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var state = await client.GetSearchAsync(search.Id, ct);
            if (state is null || state.IsComplete)
                break;
            await Task.Delay(TimeSpan.FromMilliseconds(opts.SearchPollIntervalMs), ct);
        }

        return await client.GetSearchResponsesAsync(search.Id, ct);
    }

    /// <summary>
    /// Fetches one candidate to completion. Success returns the file's new path inside
    /// <paramref name="destinationDirectory"/>; anything else returns
    /// <see cref="DownloadResult.Missing"/> (peer rejected / errored / stalled) so the caller can
    /// try the next candidate.
    /// </summary>
    public async Task<DownloadResult> FetchCandidateAsync(
        SlskdCandidate candidate, string destinationDirectory, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        var file = candidate.File;
        var enqueued = await client.EnqueueDownloadAsync(candidate.Username, file.Filename, file.Size, ct);
        if (!enqueued)
            return DownloadResult.Missing($"peer {candidate.Username} rejected the download request");

        var startedAt = DateTime.UtcNow;
        var deadline = startedAt.AddSeconds(opts.DownloadTimeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(opts.TransferPollIntervalSeconds), ct);

            var transfers = await client.GetDownloadsAsync(candidate.Username, ct);
            var transfer = transfers.FirstOrDefault(t =>
                string.Equals(t.Filename, file.Filename, StringComparison.OrdinalIgnoreCase));
            if (transfer is null)
                continue; // enqueue may not be visible yet

            if (transfer.IsSucceeded)
            {
                var localPath = LocateCompletedFile(opts.DownloadsDirectory, file, startedAt);
                if (localPath is null)
                {
                    logger.LogWarning(
                        "slskd reported success but no local file found for '{Remote}' under {Dir}",
                        LogSanitizer.ForLog(file.RemoteLeafName), LogSanitizer.ForLog(opts.DownloadsDirectory));
                    return DownloadResult.Missing("completed file not found in slskd downloads directory");
                }
                var finalPath = MoveIntoDestination(localPath, destinationDirectory, file.NormalizedExtension);
                return DownloadResult.Ok(finalPath);
            }

            if (transfer.IsErrored)
                return DownloadResult.Missing($"transfer from {candidate.Username} ended: {transfer.State}");
        }

        // Stalled past the ceiling — cancel server-side so slskd doesn't keep the slot occupied.
        await TryCancelAsync(candidate.Username, file.Filename, ct);
        return DownloadResult.Missing($"transfer from {candidate.Username} timed out");
    }

    private async Task TryCancelAsync(string username, string filename, CancellationToken ct)
    {
        try
        {
            var transfers = await client.GetDownloadsAsync(username, ct);
            var transfer = transfers.FirstOrDefault(t =>
                string.Equals(t.Filename, filename, StringComparison.OrdinalIgnoreCase));
            if (transfer is not null)
                await client.CancelDownloadAsync(username, transfer.Id, remove: true, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to cancel stalled slskd transfer from {Username}", LogSanitizer.ForLog(username));
        }
    }

    /// <summary>
    /// Finds the completed file on disk. slskd nests downloads by remote folder and exposes no local
    /// path over the API, so match by leaf name + exact size, newest first, preferring files written
    /// since this attempt started.
    /// </summary>
    internal static string? LocateCompletedFile(string downloadsDirectory, SlskdFile file, DateTime startedAtUtc)
    {
        if (!Directory.Exists(downloadsDirectory))
            return null;

        var leaf = file.RemoteLeafName;
        return Directory
            .EnumerateFiles(downloadsDirectory, "*", SearchOption.AllDirectories)
            .Where(p => string.Equals(Path.GetFileName(p), leaf, StringComparison.OrdinalIgnoreCase))
            .Select(p => new FileInfo(p))
            .Where(f => f.Length == file.Size)
            .OrderByDescending(f => f.LastWriteTimeUtc >= startedAtUtc.AddMinutes(-1))
            .ThenByDescending(f => f.LastWriteTimeUtc)
            .Select(f => f.FullName)
            .FirstOrDefault();
    }

    /// <summary>
    /// Moves the finished file into the pipeline's staging dir under a unique stem (the identity
    /// gets stamped into tags, so the on-disk name is throwaway). Cross-volume safe.
    /// </summary>
    private static string MoveIntoDestination(string sourcePath, string destinationDirectory, string extension)
    {
        Directory.CreateDirectory(destinationDirectory);
        var target = Path.Combine(destinationDirectory, Guid.NewGuid().ToString("N") + extension);
        try
        {
            File.Move(sourcePath, target);
        }
        catch (IOException)
        {
            // Cross-device move (staging volume ≠ download volume): copy then best-effort delete.
            File.Copy(sourcePath, target, overwrite: false);
            try { File.Delete(sourcePath); } catch (IOException) { /* stale staging file is harmless */ }
        }
        return target;
    }
}
