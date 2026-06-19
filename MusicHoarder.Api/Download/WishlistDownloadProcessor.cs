using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Download;

/// <summary>
/// The testable core of the wishlist downloader: processes one batch of Pending items against a
/// provided <see cref="MusicHoarderDbContext"/> and links already-downloaded items to their ingested
/// library song. The <see cref="DownloadBackgroundService"/> owns the loop/scoping and delegates here.
/// </summary>
public class WishlistDownloadProcessor(
    IEnumerable<IDownloadProvider> downloadProviders,
    DownloadProgressTracker progressTracker,
    IOptions<MusicEnricherOptions> options,
    ILogger<WishlistDownloadProcessor> logger)
{
    public const int BatchSize = 20;

    /// <summary>
    /// Processes up to <see cref="BatchSize"/> Pending items: skips exact already-owned tracks, downloads
    /// the rest via the resolved provider, and persists the resulting status transitions. Returns the
    /// number of items processed and how many produced a new file.
    /// </summary>
    public async Task<(int Processed, int Downloaded)> ProcessBatchAsync(
        MusicHoarderDbContext db, Guid ownerId, CancellationToken ct)
    {
        var opts = options.Value;
        var destinationDir = opts.DownloadDirectory;

        var batch = await db.WishlistItems
            .IgnoreQueryFilters()
            .Where(w => w.OwnerUserId == ownerId && w.OwnerUserId != WellKnownUsers.DemoId)
            .Where(w => w.Status == WishlistItemStatus.Pending)
            .OrderByDescending(w => w.SpotifyAddedAtUtc)
            .ThenBy(w => w.Id)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (batch.Count == 0) return (0, 0);

        var trackIds = batch.Select(w => w.SpotifyTrackId).ToList();
        var matches = await db.SpotifyTrackLibraryMatches
            .IgnoreQueryFilters()
            .Where(m => m.OwnerUserId == ownerId && trackIds.Contains(m.SpotifyTrackId))
            .ToDictionaryAsync(m => m.SpotifyTrackId, ct);

        var provider = ResolveProvider();
        var inLibrary = (int)ComparisonMatchStatus.InLibrary;
        var now = DateTime.UtcNow;

        // Partition: tracks with an exact in-library match are skipped (no download); the rest go out.
        var owned = new List<(WishlistItem Item, int? SongId)>();
        var toDownload = new List<WishlistItem>();
        foreach (var item in batch)
        {
            if (matches.TryGetValue(item.SpotifyTrackId, out var match) && match.MatchStatus == inLibrary)
                owned.Add((item, match.MatchedSongId));
            else
                toDownload.Add(item);
        }

        foreach (var (item, songId) in owned)
        {
            item.Status = WishlistItemStatus.SkippedOwned;
            item.DownloadedSongId = songId;
            item.UpdatedAtUtc = now;
            progressTracker.IncrementSkipped();
        }

        // Surface in-flight work: persist Downloading before the (slow) fetch so the UI's Downloading
        // tab/badge reflects what's actually running, instead of items jumping straight to a terminal
        // state. Unresolved items (run cancelled mid-batch) are reverted to Pending below, and a crash
        // leaves them Downloading until ResetStaleDownloadingAsync reclaims them on the next run.
        if (toDownload.Count > 0)
        {
            foreach (var item in toDownload)
            {
                item.Status = WishlistItemStatus.Downloading;
                item.UpdatedAtUtc = now;
            }
            await db.SaveChangesAsync(ct);
        }

        // Downloads hit the network/disk only — no DB access — so the parallel section is EF-safe.
        // Parallel.ForEachAsync already bounds in-flight bodies to MaxDegreeOfParallelism, so no extra
        // semaphore is needed.
        var results = new Dictionary<int, DownloadResult>();
        var resultsLock = new object();
        if (toDownload.Count > 0)
        {
            await Parallel.ForEachAsync(
                toDownload,
                new ParallelOptions { MaxDegreeOfParallelism = opts.DownloadConcurrency, CancellationToken = ct },
                async (item, token) =>
                {
                    var req = new DownloadRequest(item.Artist, item.Title, item.Album, item.Isrc, item.DurationMs, destinationDir);
                    var result = await provider.DownloadAsync(req, token);
                    lock (resultsLock) results[item.Id] = result;
                });
        }

        // Re-read the clock after the (potentially minutes-long, throttled) fetch so terminal
        // UpdatedAtUtc stamps reflect when each item actually finished, not when the batch started.
        var finishedAt = DateTime.UtcNow;
        var downloadedCount = 0;
        foreach (var item in toDownload)
        {
            if (!results.TryGetValue(item.Id, out var result))
            {
                // Cancelled before this item ran — revert the optimistic Downloading mark to Pending
                // so the next run retries it (without inflating AttemptCount).
                item.Status = WishlistItemStatus.Pending;
                item.UpdatedAtUtc = finishedAt;
                continue;
            }

            item.DownloadProvider = provider.Name;
            item.AttemptCount += 1;
            item.UpdatedAtUtc = finishedAt;

            if (result.Success && result.FilePath is not null)
            {
                item.Status = WishlistItemStatus.Downloaded;
                item.DownloadedFilePath = NormalizePath(result.FilePath);
                item.LastError = null;
                downloadedCount++;
                progressTracker.IncrementDownloaded();
            }
            else if (result.NotFound)
            {
                item.Status = WishlistItemStatus.NotFound;
                item.LastError = result.Error;
                progressTracker.IncrementNotFound();
            }
            else
            {
                item.Status = WishlistItemStatus.Failed;
                item.LastError = result.Error;
                progressTracker.IncrementFailed();
            }
        }

        await db.SaveChangesAsync(ct);
        return (batch.Count, downloadedCount);
    }

    /// <summary>
    /// Links Downloaded items to the library song the scanner created for their file (matching
    /// <see cref="SongMetadata.SourcePath"/> to <see cref="WishlistItem.DownloadedFilePath"/>). Also
    /// heals items whose previously-linked song was soft-deleted: re-links to a live song if the file
    /// was re-scanned, otherwise clears the now-dangling link (a soft-delete leaves the FK intact since
    /// the row isn't physically removed, so this is the only place the stale reference gets cleaned up).
    /// Returns how many were newly linked.
    /// </summary>
    public async Task<int> LinkDownloadedItemsAsync(MusicHoarderDbContext db, Guid ownerId, CancellationToken ct)
    {
        var unlinked = await db.WishlistItems
            .IgnoreQueryFilters()
            .Where(w => w.OwnerUserId == ownerId
                && w.Status == WishlistItemStatus.Downloaded
                && w.DownloadedFilePath != null
                && (w.DownloadedSongId == null
                    || (w.DownloadedSong != null && w.DownloadedSong.DeletedAtUtc != null)))
            .ToListAsync(ct);

        if (unlinked.Count == 0) return 0;

        var paths = unlinked.Select(w => w.DownloadedFilePath!).ToList();
        var songs = await db.Songs
            .IgnoreQueryFilters()
            .Where(s => s.OwnerUserId == ownerId && s.DeletedAtUtc == null && paths.Contains(s.SourcePath))
            .Select(s => new { s.Id, s.SourcePath })
            .ToListAsync(ct);
        var byPath = songs
            .GroupBy(s => s.SourcePath)
            .ToDictionary(g => g.Key, g => g.First().Id);

        var linked = 0;
        var changed = false;
        var now = DateTime.UtcNow;
        foreach (var item in unlinked)
        {
            if (item.DownloadedFilePath is { } p && byPath.TryGetValue(p, out var songId))
            {
                if (item.DownloadedSongId == songId) continue; // already linked to the live song
                item.DownloadedSongId = songId;
                item.UpdatedAtUtc = now;
                linked++;
                changed = true;
            }
            else if (item.DownloadedSongId != null)
            {
                // Was linked to a since-soft-deleted song and no live song exists at the path anymore —
                // drop the dangling link so the row reads "downloaded, not in library" instead of
                // pointing at an invisible song (and so it re-links if the file is re-scanned later).
                item.DownloadedSongId = null;
                item.UpdatedAtUtc = now;
                changed = true;
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync(ct);
            if (linked > 0)
                logger.LogInformation("Linked {Count} downloaded wishlist items to library songs", linked);
        }
        return linked;
    }

    /// <summary>
    /// Reverts items stuck in <see cref="WishlistItemStatus.Downloading"/> back to Pending. Items are
    /// only transiently Downloading during an active batch, so any found between runs are leftovers
    /// from a crash/restart mid-fetch — reclaim them so they retry. Returns how many were reset.
    /// </summary>
    public async Task<int> ResetStaleDownloadingAsync(MusicHoarderDbContext db, Guid ownerId, CancellationToken ct)
    {
        var stale = await db.WishlistItems
            .IgnoreQueryFilters()
            .Where(w => w.OwnerUserId == ownerId && w.Status == WishlistItemStatus.Downloading)
            .ToListAsync(ct);

        if (stale.Count == 0) return 0;

        var now = DateTime.UtcNow;
        foreach (var item in stale)
        {
            item.Status = WishlistItemStatus.Pending;
            item.UpdatedAtUtc = now;
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Reset {Count} stale Downloading wishlist items to Pending", stale.Count);
        return stale.Count;
    }

    public IDownloadProvider ResolveProvider()
    {
        var name = options.Value.DownloadProvider;
        return downloadProviders.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? downloadProviders.First();
    }

    /// <summary>Normalize to forward slashes to match how the scanner stores <see cref="SongMetadata.SourcePath"/>.</summary>
    private static string NormalizePath(string path) => path.Replace('\\', '/');
}
