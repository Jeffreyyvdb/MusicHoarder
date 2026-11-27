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
    /// every build, so it is never adopted; nor is anything under the destination root.
    /// The job takes its pages from this, and the Background work row counts with it.
    /// </summary>
    public static IQueryable<SongMetadata> WhereAdoptable(this IQueryable<SongMetadata> songs, MusicEnricherOptions o)
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
            .Where(s => !hasDestination || !s.SourcePath.StartsWith(destination));
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
            .WhereAdoptable(opts)
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

        foreach (var song in page)
        {
            if (read.TryGetValue(song.Id, out var facts))
                await registry.LinkAsync(db, song, facts, ct);
        }
        await db.SaveChangesAsync(ct);

        if (read.Count < page.Count)
            logger.LogInformation(
                "Audio asset adoption left {Skipped} of {Count} songs for a later pass (missing, changed or still being written)",
                page.Count - read.Count, page.Count);
        return new AudioAssetAdoptionPage(page[^1].Id, page.Count, read.Count);
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
