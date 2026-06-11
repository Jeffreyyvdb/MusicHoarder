using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Library;

/// <summary>
/// One-time, on-startup seed that records each already-built track's CURRENT tags as its
/// <see cref="SongMetadata.LastWrittenTagsJson"/> snapshot — WITHOUT emitting any
/// <see cref="LibraryWriteEvent"/>. It makes the destination-write History feature accurate for a
/// library that was built before the feature shipped: the first re-tag after deploy then diffs against
/// the track's actual current tags instead of the source-original baseline, so it reports the real
/// "since last time" change set rather than a flood of enriched-vs-source rows.
///
/// Runs after migrations (it's a <see cref="BackgroundService"/>, so it never blocks startup) and is
/// gated by <see cref="RuntimeSettings.LibraryWriteBaselineCompletedAtUtc"/> so it runs at most once;
/// every step is idempotent (only fills a null snapshot), so a crashed run simply resumes next boot.
/// </summary>
public sealed class LibraryWriteBaselineBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<MusicEnricherOptions> options,
    ILogger<LibraryWriteBaselineBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (await IsAlreadyDoneAsync(stoppingToken))
            {
                return;
            }

            var batchSize = options.Value.LibraryBuilderBatchSize;
            var cursor = 0;
            var totalSeeded = 0;

            while (!stoppingToken.IsCancellationRequested)
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

                // Keyset-paginate by Id (background services bypass the per-user filter). Only already-built
                // real tracks that don't yet have a snapshot.
                var batch = await db.Songs
                    .IgnoreQueryFilters()
                    .Where(s => !s.IsSynthetic
                        // Demo rows are never re-tagged, so a baseline snapshot is pointless.
                        && s.OwnerUserId != WellKnownUsers.DemoId
                        && s.DeletedAtUtc == null
                        && s.LibraryBuildStatus == LibraryBuildStatus.Done
                        && s.DestinationPath != null
                        && s.LastWrittenTagsJson == null
                        && s.Id > cursor)
                    .OrderBy(s => s.Id)
                    .Take(batchSize)
                    .ToListAsync(stoppingToken);

                if (batch.Count == 0)
                {
                    break;
                }

                cursor = batch[^1].Id;

                foreach (var song in batch)
                {
                    // The song's own current row tags are the best available approximation of what is on
                    // disk; the next real re-tag diffs against this and self-corrects.
                    var current = WrittenTagSet.From(song, AlbumIdentity.FromSong(song));
                    song.LastWrittenTagsJson = JsonSerializer.Serialize(current);
                    totalSeeded++;
                }

                await db.SaveChangesAsync(stoppingToken);
            }

            stoppingToken.ThrowIfCancellationRequested();
            await MarkDoneAsync(stoppingToken);
            logger.LogInformation("Library-write baseline complete: seeded {Seeded} built tracks.", totalSeeded);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutting down mid-run — leave the marker unset so it resumes next boot.
        }
        catch (Exception ex)
        {
            // Never crash startup; leave the marker unset so the seed retries on the next boot.
            logger.LogError(ex, "Library-write baseline failed; will retry on next startup.");
        }
    }

    private async Task<bool> IsAlreadyDoneAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var row = await db.RuntimeSettings.AsNoTracking().OrderBy(r => r.Id).FirstOrDefaultAsync(ct);
        return row?.LibraryWriteBaselineCompletedAtUtc is not null;
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

        row.LibraryWriteBaselineCompletedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
