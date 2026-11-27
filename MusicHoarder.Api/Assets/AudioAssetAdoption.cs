using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Scanner;

namespace MusicHoarder.Api.Assets;

public static class AudioAssetAdoptionQueries
{
    /// <summary>
    /// Songs the adoption job still has to link: live, real, with no asset, and with a source file that
    /// is an untouched original, under the source share or the download folder. A row whose staged
    /// source was released has only its destination copy left, and that copy's tags are rewritten by
    /// every build, so it is never adopted; nor is anything under the destination root, nor a quality
    /// upgrade's download still waiting to be merged (it is deleted if the merge is refused).
    /// The job takes its pages from this, and the Background work row counts with it.
    /// </summary>
    public static IQueryable<SongMetadata> WhereAdoptable(
        this IQueryable<SongMetadata> songs, MusicEnricherOptions o, IQueryable<UpgradeRequest> upgradeRequests)
    {
        var hasSource = !string.IsNullOrWhiteSpace(o.SourceDirectory);
        var source = hasSource ? IndexService.NormalizeRootPrefix(o.SourceDirectory) : "/";
        var hasDownloads = !string.IsNullOrWhiteSpace(o.DownloadDirectory);
        var downloads = hasDownloads ? IndexService.NormalizeRootPrefix(o.DownloadDirectory) : "/";
        var hasDestination = !string.IsNullOrWhiteSpace(o.DestinationDirectory);
        var destination = hasDestination ? IndexService.NormalizeRootPrefix(o.DestinationDirectory) : "/";

        return songs
            .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic && s.AudioAssetId == null && s.SourceReleasedAtUtc == null)
            .ExcludingDemoTenant()
            .Where(s => (hasSource && s.SourcePath.StartsWith(source)) || (hasDownloads && s.SourcePath.StartsWith(downloads)))
            .Where(s => !hasDestination || !s.SourcePath.StartsWith(destination))
            .Where(s => !upgradeRequests.Any(r =>
                r.Status == UpgradeRequestStatus.AwaitingIngest
                && r.OwnerUserId == s.OwnerUserId
                && r.DownloadedFilePath == s.SourcePath));
    }
}

/// <summary>What one page of adoption did.</summary>
public sealed record AudioAssetAdoptionPage(int LastSongId, int Examined, int Linked);

/// <summary>
/// Registers the files songs already have as <see cref="AudioAsset"/>s, where they are. It only reads:
/// nothing is moved, renamed or tag-written. Run by <see cref="AudioAssetAdoptionBackgroundService"/>
/// a page at a time; a linked song leaves <see cref="AudioAssetAdoptionQueries.WhereAdoptable"/>, so a
/// restart carries on where the last page saved.
/// </summary>
public sealed class AudioAssetAdoptionService(
    MusicHoarderDbContext db,
    AudioAssetRegistry registry,
    IOptions<MusicEnricherOptions> options,
    TimeProvider time,
    ILogger<AudioAssetAdoptionService> logger)
{
    /// <summary>Adopts the next page of songs after <paramref name="afterSongId"/>. Null when none are left.</summary>
    public async Task<AudioAssetAdoptionPage?> AdoptNextPageAsync(int afterSongId, CancellationToken ct)
    {
        var opts = options.Value;
        var page = await db.Songs
            .IgnoreQueryFilters()
            .WhereAdoptable(opts, db.UpgradeRequests.IgnoreQueryFilters())
            .Where(s => s.Id > afterSongId)
            .OrderBy(s => s.Id)
            .Take(opts.AudioAssetAdoptionBatchSize)
            .ToListAsync(ct);
        if (page.Count == 0)
            return null;

        var settledBefore = time.GetUtcNow().UtcDateTime.AddSeconds(-opts.ScanSettleSeconds);
        var read = new ConcurrentDictionary<int, AudioFileFacts>();
        await Parallel.ForEachAsync(
            page,
            new ParallelOptions { MaxDegreeOfParallelism = opts.AudioAssetAdoptionConcurrency, CancellationToken = ct },
            async (song, token) =>
            {
                if (!IsSettledAndIndexed(song, settledBefore))
                    return;
                if (await registry.ReadAsync(song.SourcePath, token) is { } facts && facts.SizeBytes == song.FileSizeBytes)
                    read[song.Id] = facts;
            });

        // Hashing a page over a network mount takes a while; the release sweep, an upgrade or a scan
        // may have moved a row on meanwhile. Re-read under the claim, which the release also holds.
        var linked = 0;
        using (await AudioAssetRegistry.ClaimAsync(ct))
        {
            var ids = read.Keys.ToList();
            var stillAdoptable = (await db.Songs
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .WhereAdoptable(opts, db.UpgradeRequests.IgnoreQueryFilters())
                    .Where(s => ids.Contains(s.Id))
                    .Select(s => new { s.Id, s.SourcePath })
                    .ToListAsync(ct))
                .ToDictionary(s => s.Id, s => s.SourcePath);
            foreach (var song in page)
            {
                if (read.TryGetValue(song.Id, out var facts)
                    && stillAdoptable.TryGetValue(song.Id, out var path)
                    && string.Equals(path, song.SourcePath, StringComparison.Ordinal))
                {
                    await registry.LinkAsync(db, song, facts, ct);
                    linked++;
                }
            }
            await db.SaveChangesAsync(ct);
        }

        if (linked < page.Count)
            logger.LogInformation(
                "Audio asset adoption left {Skipped} of {Count} songs for a later pass (missing, changed or still being written)",
                page.Count - linked, page.Count);
        return new AudioAssetAdoptionPage(page[^1].Id, page.Count, linked);
    }

    /// <summary>
    /// The file is there, untouched for <see cref="MusicEnricherOptions.ScanSettleSeconds"/>, and still
    /// the size the scanner indexed. A file that changed since is left for the scanner to re-index first.
    /// </summary>
    private static bool IsSettledAndIndexed(SongMetadata song, DateTime settledBefore)
    {
        var file = new FileInfo(song.SourcePath);
        return file.Exists && file.LastWriteTimeUtc <= settledBefore && file.Length == song.FileSizeBytes;
    }
}
