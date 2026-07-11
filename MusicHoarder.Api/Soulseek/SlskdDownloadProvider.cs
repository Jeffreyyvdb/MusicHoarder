using Microsoft.Extensions.Options;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Soulseek;

/// <summary>
/// Downloads a track from the Soulseek network via a user-operated slskd instance. Searches, elects
/// the best candidates (<see cref="SlskdCandidateSelector"/>), and polls the transfer to completion —
/// trying the next-best peer when one stalls or errors. The finished file is moved out of slskd's
/// staging directory into the request's destination (the normal download staging dir), so the caller's
/// tag-stamp + scan pipeline applies unchanged.
/// <para>
/// Unconfigured or no acceptable result ⇒ <see cref="DownloadResult.Missing"/> so the provider chain
/// falls through (e.g. to yt-dlp). Only a transport-level slskd failure returns
/// <see cref="DownloadResult.Failed"/>, which stops the chain — a flaky slskd shouldn't silently burn
/// the fallback provider's quota on every track.
/// </para>
/// </summary>
public class SlskdDownloadProvider(
    ISlskdClient client,
    IOptionsMonitor<SlskdOptions> options,
    ILogger<SlskdDownloadProvider> logger) : IDownloadProvider
{
    public string Name => "slskd";

    public async Task<DownloadResult> DownloadAsync(DownloadRequest req, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        if (!opts.IsConfigured)
            return DownloadResult.Missing("slskd not configured");

        try
        {
            var primaryQuery = BuildSearchQuery(req.Artist, req.Title);
            var responses = await RunSearchAsync(primaryQuery, opts, ct);
            var candidates = SlskdCandidateSelector.Select(responses, req.Title, req.DurationMs, opts);

            // Retry pass: some peers only share album folders whose paths carry the album name but
            // abbreviate the track title. An artist+album search surfaces those; the selector's
            // title-token filter still picks the right file out of the folder.
            if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(req.Album))
            {
                var albumQuery = BuildSearchQuery(req.Artist, req.Album!);
                responses = await RunSearchAsync(albumQuery, opts, ct);
                candidates = SlskdCandidateSelector.Select(responses, req.Title, req.DurationMs, opts);
            }

            if (candidates.Count == 0)
            {
                logger.LogInformation("slskd found no acceptable candidate for '{Artist} - {Title}'",
                    LogSanitizer.ForLog(req.Artist), LogSanitizer.ForLog(req.Title));
                return DownloadResult.Missing("no acceptable Soulseek result");
            }

            string? lastError = null;
            foreach (var candidate in candidates.Take(Math.Max(1, opts.MaxCandidateAttempts)))
            {
                ct.ThrowIfCancellationRequested();
                var outcome = await TryDownloadCandidateAsync(candidate, req, opts, ct);
                if (outcome.Success)
                    return outcome;
                lastError = outcome.Error;
            }

            // Candidates existed but none delivered (busy queues, dead peers). NotFound —
            // not a slskd fault — so the chain may fall through and the sweep can retry later.
            return DownloadResult.Missing(lastError ?? "all Soulseek candidates failed");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "slskd download failed for '{Artist} - {Title}'",
                LogSanitizer.ForLog(req.Artist), LogSanitizer.ForLog(req.Title));
            return DownloadResult.Failed(ex.Message);
        }
    }

    internal static string BuildSearchQuery(string artist, string term) =>
        string.IsNullOrWhiteSpace(artist) ? term.Trim() : $"{artist} {term}".Trim();

    /// <summary>Starts a search and polls until slskd reports it complete (or our ceiling passes).</summary>
    private async Task<IReadOnlyList<SlskdSearchResponse>> RunSearchAsync(
        string query, SlskdOptions opts, CancellationToken ct)
    {
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

    private async Task<DownloadResult> TryDownloadCandidateAsync(
        SlskdCandidate candidate, DownloadRequest req, SlskdOptions opts, CancellationToken ct)
    {
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
                var finalPath = MoveIntoDestination(localPath, req.DestinationDirectory, file.NormalizedExtension);
                logger.LogInformation("slskd downloaded '{Artist} - {Title}' from {Username} ({Size} bytes)",
                    LogSanitizer.ForLog(req.Artist), LogSanitizer.ForLog(req.Title),
                    LogSanitizer.ForLog(candidate.Username), file.Size);
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
    /// path over the API, so match by leaf name + exact size, newest first, ignoring files that
    /// predate this attempt (a stale identical download from an earlier run would still be fine, but
    /// the recency filter keeps us from grabbing a half-written unrelated twin).
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
    /// Moves the finished file into the pipeline's staging dir under a unique stem (mirrors the
    /// yt-dlp provider — the identity gets stamped into tags, so the on-disk name is throwaway).
    /// Cross-volume safe: File.Move falls back to copy+delete semantics via explicit copy.
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
