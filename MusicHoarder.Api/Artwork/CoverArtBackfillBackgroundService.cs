using System.Collections.Concurrent;
using System.IO.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Artwork;

/// <summary>
/// One-time, on-startup backfill that makes the album-artwork feature take effect for a library that
/// was already scanned and built before the feature shipped. It runs after migrations (it's a
/// <see cref="BackgroundService"/>, so it never blocks startup) and:
/// <list type="bullet">
///   <item>sets <see cref="SongMetadata.HasCoverArt"/> for every existing real track (the scanner only
///   touches changed files, so it would otherwise never repopulate the back catalogue), and</item>
///   <item>writes a destination <c>cover.&lt;ext&gt;</c> into each already-built album folder that lacks
///   one (the library builder skips <c>Done</c> albums).</item>
/// </list>
/// Gated by a persisted marker (<see cref="RuntimeSettings.CoverArtBackfillCompletedAtUtc"/>) so it runs
/// at most once; every step is idempotent, so a crashed run simply resumes on the next boot.
/// </summary>
public sealed class CoverArtBackfillBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<MusicEnricherOptions> options,
    ILogger<CoverArtBackfillBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (await IsAlreadyDoneAsync(stoppingToken))
            {
                return;
            }

            var opts = options.Value;
            var processedDirectories = new HashSet<string>(StringComparer.Ordinal);
            var cursor = 0;
            int totalSeen = 0, totalFlagged = 0, totalCovers = 0;

            while (!stoppingToken.IsCancellationRequested)
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
                var resolver = scope.ServiceProvider.GetRequiredService<ICoverArtResolver>();
                var coverWriter = scope.ServiceProvider.GetRequiredService<IAlbumCoverWriter>();
                var fileSystem = scope.ServiceProvider.GetRequiredService<IFileSystem>();

                // Keyset-paginate by Id (background services bypass the per-user filter; single-owner app).
                // Demo rows are skipped — their source files are a read-only mount, so neither the
                // HasCoverArt flag nor a destination cover write should target them.
                var batch = await db.Songs
                    .IgnoreQueryFilters()
                    .Where(s => !s.IsSynthetic && s.DeletedAtUtc == null && s.Id > cursor
                        && s.OwnerUserId != WellKnownUsers.DemoId)
                    .OrderBy(s => s.Id)
                    .Take(opts.LibraryBuilderBatchSize)
                    .ToListAsync(stoppingToken);

                if (batch.Count == 0)
                {
                    break;
                }

                cursor = batch[^1].Id;
                totalSeen += batch.Count;

                // Resolve artwork presence in parallel (read-only file IO — the resolver is stateless and
                // the DbContext is untouched here); entity mutation happens afterwards on this thread.
                var hasArtById = new ConcurrentDictionary<int, bool>();
                await Parallel.ForEachAsync(
                    batch.Select(s => (s.Id, s.SourcePath)),
                    new ParallelOptions { MaxDegreeOfParallelism = opts.SmbConcurrency, CancellationToken = stoppingToken },
                    (row, _) =>
                    {
                        hasArtById[row.Id] = !string.IsNullOrEmpty(row.SourcePath) && resolver.HasArtwork(row.SourcePath);
                        return ValueTask.CompletedTask;
                    });

                foreach (var song in batch)
                {
                    if (hasArtById.TryGetValue(song.Id, out var hasArt) && song.HasCoverArt != hasArt)
                    {
                        song.HasCoverArt = hasArt;
                        totalFlagged++;
                    }
                }

                await db.SaveChangesAsync(stoppingToken);

                // One destination cover per distinct built, non-unreleased album folder not yet handled.
                var coverDirs = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
                foreach (var song in batch)
                {
                    if (song.LibraryBuildStatus != LibraryBuildStatus.Done
                        || song.IsUnreleased
                        || string.IsNullOrEmpty(song.DestinationPath)
                        || string.IsNullOrEmpty(song.SourcePath))
                    {
                        continue;
                    }

                    var dir = fileSystem.Path.GetDirectoryName(song.DestinationPath);
                    if (!string.IsNullOrEmpty(dir) && !processedDirectories.Contains(dir))
                    {
                        coverDirs.TryAdd(dir, song.SourcePath);
                    }
                }

                if (!coverDirs.IsEmpty)
                {
                    foreach (var dir in coverDirs.Keys)
                    {
                        processedDirectories.Add(dir);
                    }

                    var written = 0;
                    await Parallel.ForEachAsync(
                        coverDirs,
                        new ParallelOptions { MaxDegreeOfParallelism = opts.LibraryBuilderWorkerConcurrency, CancellationToken = stoppingToken },
                        (entry, _) =>
                        {
                            if (coverWriter.WriteIfMissing(entry.Key, entry.Value))
                            {
                                Interlocked.Increment(ref written);
                            }
                            return ValueTask.CompletedTask;
                        });
                    totalCovers += written;
                }
            }

            stoppingToken.ThrowIfCancellationRequested();
            await MarkDoneAsync(stoppingToken);
            logger.LogInformation(
                "Cover-art backfill complete: scanned {Seen} tracks, flagged {Flagged} with art, wrote {Covers} album covers.",
                totalSeen, totalFlagged, totalCovers);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutting down mid-run — leave the marker unset so it resumes next boot.
        }
        catch (Exception ex)
        {
            // Never crash startup; leave the marker unset so the backfill retries on the next boot.
            logger.LogError(ex, "Cover-art backfill failed; will retry on next startup.");
        }
    }

    private async Task<bool> IsAlreadyDoneAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var row = await db.RuntimeSettings.AsNoTracking().OrderBy(r => r.Id).FirstOrDefaultAsync(ct);
        return row?.CoverArtBackfillCompletedAtUtc is not null;
    }

    private async Task MarkDoneAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        // Same get-or-create as RuntimeSettingsService so we keep the singleton settings row.
        var row = await db.RuntimeSettings.OrderBy(r => r.Id).FirstOrDefaultAsync(ct);
        if (row is null)
        {
            row = new RuntimeSettings();
            db.RuntimeSettings.Add(row);
        }

        row.CoverArtBackfillCompletedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
