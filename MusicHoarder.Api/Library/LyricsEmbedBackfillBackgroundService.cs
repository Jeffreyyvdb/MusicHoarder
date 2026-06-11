using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Pipeline;

namespace MusicHoarder.Api.Library;

/// <summary>
/// One-time, on-startup heal that re-queues every already-built track whose destination file is missing
/// the lyrics MusicHoarder's DB holds. Before the lyrics-before-build gate (see
/// <see cref="LibraryBuildQuery"/>), enrichment committed <c>Matched</c> before the async LRCLIB fetch
/// returned, so the builder could copy+tag a file in that window and the lyrics never reached disk — the
/// MusicHoarder UI shows them (it reads the DB) while Navidrome (which reads the file's embedded tags)
/// shows nothing.
///
/// Neither the DB row nor the <see cref="SongMetadata.LastWrittenTagsJson"/> snapshot can detect this:
/// both record what MusicHoarder *intended* to write. So this reads the actual file via
/// <see cref="IEmbeddedLyricsReader"/> as ground truth and, when the file has no embedded lyrics,
/// <see cref="SongMetadata.RequeueForRetag"/>s it — the builder then re-tags it in place with the DB
/// lyrics. Files that already carry lyrics are left untouched (no needless rewrite / Navidrome rescan).
///
/// Runs after migrations (a <see cref="BackgroundService"/>, so it never blocks startup) and is gated by
/// <see cref="RuntimeSettings.LyricsEmbedBackfillCompletedAtUtc"/> so it runs at most once. It defers
/// (without marking done, so it retries next boot) while the destination directory is offline — otherwise
/// every file would read as missing and the whole library would be re-queued.
/// </summary>
public sealed class LyricsEmbedBackfillBackgroundService(
    IServiceScopeFactory scopeFactory,
    IEmbeddedLyricsReader lyricsReader,
    IDirectoryAvailability directoryAvailability,
    IOptions<MusicEnricherOptions> options,
    ILogger<LyricsEmbedBackfillBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (await IsAlreadyDoneAsync(stoppingToken))
            {
                return;
            }

            // Reading destination files only makes sense when they're reachable; otherwise File.Exists
            // is false for everything and we'd re-queue the entire library. Leave the marker unset so
            // the heal runs on a later boot once the library is mounted.
            if (!directoryAvailability.Current.AllAvailable)
            {
                logger.LogInformation(
                    "Lyrics-embed backfill deferred: source/destination directory offline. Will retry next startup.");
                return;
            }

            var batchSize = options.Value.LibraryBuilderBatchSize;
            var readConcurrency = Math.Max(1, options.Value.SmbConcurrency);
            var cursor = 0;
            var scanned = 0;
            var requeued = 0;

            while (!stoppingToken.IsCancellationRequested)
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

                // Keyset-paginate by Id (background services bypass the per-user filter). Only built real
                // tracks that actually have lyrics text to embed — this excludes instrumental / not-found
                // rows, which legitimately carry no embedded lyrics.
                var batch = await db.Songs
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(s => !s.IsSynthetic
                        // Demo files live on a read-only mount with no embedded lyrics by design —
                        // re-queuing them would make the builder copy them into the owner's library.
                        && s.OwnerUserId != WellKnownUsers.DemoId
                        && s.DeletedAtUtc == null
                        && s.LibraryBuildStatus == LibraryBuildStatus.Done
                        && s.DestinationPath != null
                        && (s.SyncedLyrics != null || s.PlainLyrics != null)
                        && s.Id > cursor)
                    .OrderBy(s => s.Id)
                    .Select(s => new { s.Id, s.DestinationPath })
                    .Take(batchSize)
                    .ToListAsync(stoppingToken);

                if (batch.Count == 0)
                {
                    break;
                }

                cursor = batch[^1].Id;
                scanned += batch.Count;

                // Read the files concurrently (pure IO, no DbContext) and collect the ids whose on-disk
                // file has no embedded lyrics.
                var missing = new ConcurrentBag<int>();
                await Parallel.ForEachAsync(
                    batch,
                    new ParallelOptions { MaxDegreeOfParallelism = readConcurrency, CancellationToken = stoppingToken },
                    (row, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        if (lyricsReader.ReadEmbeddedLyrics(row.DestinationPath!) is null)
                        {
                            missing.Add(row.Id);
                        }

                        return ValueTask.CompletedTask;
                    });

                if (!missing.IsEmpty)
                {
                    var ids = missing.ToList();
                    var toRequeue = await db.Songs
                        .IgnoreQueryFilters()
                        .Where(s => ids.Contains(s.Id))
                        .ToListAsync(stoppingToken);

                    foreach (var song in toRequeue)
                    {
                        song.RequeueForRetag();
                    }

                    await db.SaveChangesAsync(stoppingToken);
                    requeued += toRequeue.Count;
                }
            }

            stoppingToken.ThrowIfCancellationRequested();
            await MarkDoneAsync(stoppingToken);
            logger.LogInformation(
                "Lyrics-embed backfill complete: scanned {Scanned} built tracks, re-queued {Requeued} missing embedded lyrics.",
                scanned, requeued);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutting down mid-run — leave the marker unset so it resumes next boot.
        }
        catch (Exception ex)
        {
            // Never crash startup; leave the marker unset so the heal retries on the next boot.
            logger.LogError(ex, "Lyrics-embed backfill failed; will retry on next startup.");
        }
    }

    private async Task<bool> IsAlreadyDoneAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var row = await db.RuntimeSettings.AsNoTracking().OrderBy(r => r.Id).FirstOrDefaultAsync(ct);
        return row?.LyricsEmbedBackfillCompletedAtUtc is not null;
    }

    private async Task MarkDoneAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var row = await db.RuntimeSettings.OrderBy(r => r.Id).FirstOrDefaultAsync(ct);
        if (row is null)
        {
            row = new RuntimeSettings();
            db.RuntimeSettings.Add(row);
        }

        row.LyricsEmbedBackfillCompletedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
