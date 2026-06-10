using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Artwork;

/// <summary>
/// Periodic sweep that fetches external cover art for already-built album folders still missing a
/// cover on disk — the back-catalog counterpart of the library builder's post-batch cover pass (which
/// only sees freshly built tracks). Per-folder retry cooldowns are persisted in
/// <see cref="AlbumCoverFetchAttempt"/> so transient provider failures retry sooner than clean
/// "no provider has it" misses, and restarts don't re-hammer the providers. Folders are processed
/// sequentially: the per-provider rate limiters dominate throughput, and iTunes' backoff window is
/// shared with the enrichment provider, so parallelism would only trip throttles.
/// </summary>
public sealed class ExternalCoverArtSweepBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<MusicEnricherOptions> options,
    ILogger<ExternalCoverArtSweepBackgroundService> logger) : BackgroundService
{
    // Let startup work (migrations, the one-time cover backfill, demo seeding) settle first.
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.EnableExternalCoverArtFetch)
        {
            logger.LogInformation("External cover art sweep disabled (EnableExternalCoverArtFetch=false)");
            return;
        }

        logger.LogInformation(
            "External cover art sweep started. Interval={IntervalMinutes}m, BatchSize={BatchSize}",
            opts.ExternalCoverArtSweepIntervalMinutes, opts.ExternalCoverArtSweepBatchSize);

        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var written = await RunSweepAsync(stoppingToken);
                if (written > 0)
                {
                    logger.LogInformation("External cover art sweep wrote {Count} covers", written);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "External cover art sweep failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(options.Value.ExternalCoverArtSweepIntervalMinutes), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task<int> RunSweepAsync(CancellationToken ct)
    {
        var opts = options.Value;
        var now = DateTime.UtcNow;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var coverArtResolver = scope.ServiceProvider.GetRequiredService<ICoverArtResolver>();
        var coverWriter = scope.ServiceProvider.GetRequiredService<IAlbumCoverWriter>();
        var fileSystem = scope.ServiceProvider.GetRequiredService<System.IO.Abstractions.IFileSystem>();

        // Built, real, non-demo tracks (background service bypasses the per-user filter). Unreleased
        // folders mix unrelated singles, so a single album cover would be wrong there. Demo rows must
        // never be swept: their destination is a read-only mount.
        // Materialize before grouping — the EF in-memory provider can't translate GroupBy here.
        var rows = await db.Songs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic
                && s.OwnerUserId != WellKnownUsers.DemoId
                && !s.IsUnreleased
                && s.LibraryBuildStatus == LibraryBuildStatus.Done
                && s.DestinationPath != null
                && s.SourcePath != null)
            .Select(s => new SweepRow(
                s.Id, s.OwnerUserId, s.SourcePath!, s.DestinationPath!, s.Album, s.AlbumArtist,
                s.MusicBrainzReleaseId, s.MusicBrainzReleaseGroupId))
            .ToListAsync(ct);

        if (rows.Count == 0)
            return 0;

        // One representative per destination album folder; prefer a track that carries a release MBID
        // (the Cover Art Archive key), then the lowest id for determinism.
        var folders = rows
            .GroupBy(r => fileSystem.Path.GetDirectoryName(r.DestinationPath) ?? string.Empty, StringComparer.Ordinal)
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .Select(g => (Folder: g.Key, Representative: g
                .OrderByDescending(r => !string.IsNullOrWhiteSpace(r.MusicBrainzReleaseId))
                .ThenBy(r => r.Id)
                .First()))
            .Where(f => fileSystem.Directory.Exists(f.Folder)
                && !coverArtResolver.DirectoryHasCoverImage(f.Folder))
            .ToList();

        if (folders.Count == 0)
            return 0;

        var folderKeys = folders.Select(f => f.Folder).ToList();
        var cooldowns = await db.AlbumCoverFetchAttempts
            .Where(a => folderKeys.Contains(a.AlbumFolder))
            .ToDictionaryAsync(a => a.AlbumFolder, StringComparer.Ordinal, ct);

        var due = folders
            .Where(f => !cooldowns.TryGetValue(f.Folder, out var attempt)
                || (attempt.NextRetryAfterUtc is not null && attempt.NextRetryAfterUtc <= now))
            .Take(opts.ExternalCoverArtSweepBatchSize)
            .ToList();

        var runId = Guid.NewGuid();
        var written = 0;
        foreach (var (folder, rep) in due)
        {
            ct.ThrowIfCancellationRequested();

            var query = new ExternalCoverArtQuery(
                rep.MusicBrainzReleaseId, rep.MusicBrainzReleaseGroupId, rep.AlbumArtist, rep.Album);
            var result = await coverWriter.WriteIfMissingAsync(folder, rep.SourcePath, query, ct);

            if (result.Written)
            {
                written++;
                if (cooldowns.TryGetValue(folder, out var resolved))
                {
                    db.AlbumCoverFetchAttempts.Remove(resolved);
                }

                db.LibraryWriteEvents.Add(new LibraryWriteEvent
                {
                    OwnerUserId = rep.OwnerUserId,
                    RunId = runId,
                    SongId = rep.Id,
                    Kind = LibraryWriteEventKind.AlbumCoverWritten,
                    WrittenAtUtc = DateTime.UtcNow,
                    AlbumFolder = folder,
                    AlbumArtist = rep.AlbumArtist,
                    Album = rep.Album,
                    FieldName = "Cover",
                    NewValue = result.Source == "source" ? "written" : $"fetched:{result.Source}",
                });
            }
            else
            {
                if (!cooldowns.TryGetValue(folder, out var attempt))
                {
                    attempt = new AlbumCoverFetchAttempt { AlbumFolder = folder };
                    cooldowns[folder] = attempt;
                    db.AlbumCoverFetchAttempts.Add(attempt);
                }

                attempt.AttemptCount++;
                attempt.LastAttemptAtUtc = DateTime.UtcNow;
                attempt.Status = result.TransientFailure ? AlbumCoverFetchStatus.Failed : AlbumCoverFetchStatus.NotFound;
                attempt.NextRetryAfterUtc = result.TransientFailure
                    ? DateTime.UtcNow.AddHours(opts.ExternalCoverArtFailedRetryHours)
                    : opts.ExternalCoverArtNotFoundRetryDays > 0
                        ? DateTime.UtcNow.AddDays(opts.ExternalCoverArtNotFoundRetryDays)
                        : null;
            }

            // Persist per folder so a cancelled sweep keeps the cooldowns/events it already earned.
            await db.SaveChangesAsync(ct);
        }

        return written;
    }

    private sealed record SweepRow(
        int Id, Guid OwnerUserId, string SourcePath, string DestinationPath,
        string? Album, string? AlbumArtist, string? MusicBrainzReleaseId, string? MusicBrainzReleaseGroupId);
}
