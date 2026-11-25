using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.BackgroundWork;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Artwork;

/// <summary>
/// Periodic sweep that fetches external cover art for already-built album folders still missing a
/// cover on disk — the back-catalog counterpart of the library builder's post-batch cover pass (which
/// only sees freshly built tracks) — and upgrades existing Cover Art Archive covers to Spotify when
/// Spotify covers are enabled and configured. Per-folder retry cooldowns are persisted in
/// <see cref="AlbumCoverFetchAttempt"/> so transient provider failures retry sooner than clean
/// "no provider has it" misses, and restarts don't re-hammer the providers. Folders are processed
/// sequentially: the per-provider rate limiters dominate throughput, and iTunes' backoff window is
/// shared with the enrichment provider, so parallelism would only trip throttles.
/// </summary>
public sealed class ExternalCoverArtSweepBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<MusicEnricherOptions> options,
    ILogger<ExternalCoverArtSweepBackgroundService> logger,
    BackgroundWorkStatus? status = null) : BackgroundService
{
    // Let startup work (migrations, demo seeding) settle first.
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

        status?.Idle(BackgroundJobIds.CoverArt, DateTime.UtcNow + InitialDelay);
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
            var interval = TimeSpan.FromMinutes(options.Value.ExternalCoverArtSweepIntervalMinutes);
            status?.Working(BackgroundJobIds.CoverArt);
            try
            {
                var written = await RunSweepAsync(stoppingToken);
                if (written > 0)
                {
                    logger.LogInformation("External cover art sweep wrote {Count} covers", written);
                }
                status?.Idle(BackgroundJobIds.CoverArt, DateTime.UtcNow + interval, worked: written > 0);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "External cover art sweep failed");
                status?.Failing(BackgroundJobIds.CoverArt, ex, DateTime.UtcNow + interval);
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
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

        // Reflect covers already on disk (e.g. fetched on a previous run, before this flag existed) back
        // into HasCoverArt so the grid/hero request them. Runs unconditionally — even when every folder
        // already has a cover and there's nothing left to fetch.
        await HealDestinationCoverFlagsAsync(db, coverArtResolver, fileSystem, ct);

        // Built, real, non-demo tracks (background service bypasses the per-user filter). Unreleased
        // folders mix unrelated singles, so a single album cover would be wrong there. Demo rows must
        // never be swept: their destination is a read-only mount.
        // Materialize before grouping — the EF in-memory provider can't translate GroupBy here.
        var rows = await db.Songs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .WhereBuiltForCoverSweep()
            .Select(s => new SweepRow(
                s.Id, s.OwnerUserId, s.SourcePath!, s.DestinationPath!, s.SourceReleasedAtUtc != null,
                s.Album, s.AlbumArtist, s.MusicBrainzReleaseId, s.MusicBrainzReleaseGroupId, s.SpotifyId))
            .ToListAsync(ct);

        if (rows.Count == 0)
            return 0;

        // One candidate per destination album folder; prefer a track carrying a Spotify id (primary
        // official-art key), then one with a release MBID (Cover Art Archive fallback), then lowest id.
        // Merge identifiers across all tracks in the folder so Spotify and MBID lookups both get a key
        // even when different tracks carry them.
        var allFolders = rows
            .GroupBy(r => fileSystem.Path.GetDirectoryName(r.DestinationPath) ?? string.Empty, StringComparer.Ordinal)
            .Where(g => !string.IsNullOrEmpty(g.Key) && fileSystem.Directory.Exists(g.Key))
            .Select(g =>
            {
                var rep = g
                    .OrderByDescending(r => !string.IsNullOrWhiteSpace(r.SpotifyId))
                    .ThenByDescending(r => !string.IsNullOrWhiteSpace(r.MusicBrainzReleaseId))
                    .ThenBy(r => r.Id)
                    .First();
                var spotifyId = g.Select(r => r.SpotifyId).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
                var mbReleaseId = g.Select(r => r.MusicBrainzReleaseId).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
                var mbReleaseGroupId = g.Select(r => r.MusicBrainzReleaseGroupId).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
                return new FolderCandidate(
                    g.Key,
                    rep,
                    new ExternalCoverArtQuery(mbReleaseId, mbReleaseGroupId, rep.AlbumArtist, rep.Album, spotifyId));
            })
            .ToList();

        if (allFolders.Count == 0)
            return 0;

        var missingFolders = new List<FolderCandidate>();
        var coveredFolders = new List<FolderCandidate>();
        foreach (var candidate in allFolders)
        {
            if (coverArtResolver.DirectoryHasCoverImage(candidate.Folder))
                coveredFolders.Add(candidate);
            else
                missingFolders.Add(candidate);
        }

        var runId = Guid.NewGuid();
        var written = 0;
        var attempted = 0;

        // Pass 1: folders missing a cover on disk.
        if (missingFolders.Count > 0)
        {
            var missingKeys = missingFolders.Select(f => f.Folder).ToList();
            var cooldowns = await db.AlbumCoverFetchAttempts
                .Where(a => missingKeys.Contains(a.AlbumFolder))
                .ToDictionaryAsync(a => a.AlbumFolder, StringComparer.Ordinal, ct);

            var due = missingFolders
                .Where(f => CoverArtSweepRules.IsMissingFolderDue(cooldowns.GetValueOrDefault(f.Folder), now))
                .Take(opts.ExternalCoverArtSweepBatchSize)
                .ToList();

            foreach (var item in due)
            {
                ct.ThrowIfCancellationRequested();
                attempted++;

                var (folder, rep, query) = item;
                // A released row has no staged source left; its destination copy still carries the
                // embedded art the tag writer copied across, so read from there instead.
                var audioPath = rep.SourceReleased ? rep.DestinationPath : rep.SourcePath;
                var result = await coverWriter.WriteIfMissingAsync(folder, audioPath, query, ct);

                if (result.Written)
                {
                    written++;

                    // The folder was a fetch candidate (it had no cover at heal time), so the heal pass
                    // above didn't flag it — do it now so the freshly written cover surfaces immediately.
                    await DestinationCoverFlagger.FlagFolderAsync(db, folder, fileSystem.Path.DirectorySeparatorChar, ct);

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
                    RecordCooldown(
                        db, cooldowns, folder, opts,
                        result.TransientFailure ? AlbumCoverFetchStatus.Failed : AlbumCoverFetchStatus.NotFound,
                        result.TransientFailure);
                }

                // Persist per folder so a cancelled sweep keeps the cooldowns/events it already earned.
                await db.SaveChangesAsync(ct);
            }
        }

        // Pass 2: upgrade existing Cover Art Archive covers to Spotify when Spotify is preferred.
        var remainingBudget = opts.ExternalCoverArtSweepBatchSize - attempted;
        if (opts.EnableSpotifyCovers && remainingBudget > 0 && coveredFolders.Count > 0)
        {
            var coveredKeys = coveredFolders.Select(f => f.Folder).ToList();
            var coverEvents = await db.LibraryWriteEvents
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(e => e.Kind == LibraryWriteEventKind.AlbumCoverWritten
                    && e.AlbumFolder != null
                    && coveredKeys.Contains(e.AlbumFolder))
                .Select(e => new CoverWriteRecord(e.Id, e.AlbumFolder!, e.WrittenAtUtc, e.NewValue))
                .ToListAsync(ct);

            var caaFolders = CoverArtSweepRules.CoverArtArchiveFolders(coverEvents);

            if (caaFolders.Count > 0)
            {
                var upgradeCandidates = coveredFolders.Where(f => caaFolders.Contains(f.Folder)).ToList();
                var upgradeKeys = upgradeCandidates.Select(f => f.Folder).ToList();
                var upgradeCooldowns = await db.AlbumCoverFetchAttempts
                    .Where(a => upgradeKeys.Contains(a.AlbumFolder))
                    .ToDictionaryAsync(a => a.AlbumFolder, StringComparer.Ordinal, ct);

                var upgradeDue = upgradeCandidates
                    .Where(f => CoverArtSweepRules.IsUpgradeDue(upgradeCooldowns.GetValueOrDefault(f.Folder), now))
                    .Take(remainingBudget)
                    .ToList();

                foreach (var (folder, rep, query) in upgradeDue)
                {
                    ct.ThrowIfCancellationRequested();

                    var result = await coverWriter.ReplaceWithSpotifyAsync(folder, query, ct);
                    if (result.Skipped)
                    {
                        // Spotify covers disabled or credentials not configured — leave remaining
                        // Cover Art Archive folders untouched with no cooldowns recorded.
                        break;
                    }

                    if (result.Written)
                    {
                        written++;

                        await DestinationCoverFlagger.FlagFolderAsync(db, folder, fileSystem.Path.DirectorySeparatorChar, ct);

                        if (upgradeCooldowns.TryGetValue(folder, out var resolved))
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
                            OldValue = CoverArtSweepRules.CoverArtArchiveSource,
                            NewValue = $"fetched:{result.Source}",
                        });
                    }
                    else
                    {
                        RecordCooldown(
                            db, upgradeCooldowns, folder, opts,
                            result.TransientFailure ? AlbumCoverFetchStatus.UpgradeFailed : AlbumCoverFetchStatus.UpgradeNotFound,
                            result.TransientFailure);
                    }

                    await db.SaveChangesAsync(ct);
                }
            }
        }

        return written;
    }

    private static void RecordCooldown(
        MusicHoarderDbContext db,
        Dictionary<string, AlbumCoverFetchAttempt> cooldowns,
        string folder,
        MusicEnricherOptions opts,
        AlbumCoverFetchStatus status,
        bool transientFailure)
    {
        if (!cooldowns.TryGetValue(folder, out var attempt))
        {
            attempt = new AlbumCoverFetchAttempt { AlbumFolder = folder };
            cooldowns[folder] = attempt;
            db.AlbumCoverFetchAttempts.Add(attempt);
        }

        attempt.AttemptCount++;
        attempt.LastAttemptAtUtc = DateTime.UtcNow;
        attempt.Status = status;
        attempt.NextRetryAfterUtc = transientFailure
            ? DateTime.UtcNow.AddHours(opts.ExternalCoverArtFailedRetryHours)
            : opts.ExternalCoverArtNotFoundRetryDays > 0
                ? DateTime.UtcNow.AddDays(opts.ExternalCoverArtNotFoundRetryDays)
                : null;
    }

    /// <summary>
    /// Sets <c>HasCoverArt = true</c> for built tracks whose destination album folder already has a
    /// cover image on disk but whose flag is still <c>false</c> — i.e. covers written before the flag
    /// was populated (or by a prior run). Loads only the still-unflagged tracks, probes each distinct
    /// folder once, and saves a single batch; in steady state it loads nothing.
    /// </summary>
    private static async Task HealDestinationCoverFlagsAsync(
        MusicHoarderDbContext db,
        ICoverArtResolver coverArtResolver,
        System.IO.Abstractions.IFileSystem fileSystem,
        CancellationToken ct)
    {
        var unflagged = await db.Songs
            .IgnoreQueryFilters()
            .ExcludingDemoTenant()
            .Where(s => !s.HasCoverArt
                && s.DeletedAtUtc == null
                && !s.IsSynthetic
                && !s.IsUnreleased
                && s.LibraryBuildStatus == LibraryBuildStatus.Done
                && s.DestinationPath != null)
            .ToListAsync(ct);

        if (unflagged.Count == 0)
            return;

        var coverByFolder = new Dictionary<string, bool>(StringComparer.Ordinal);
        var flagged = 0;
        foreach (var song in unflagged)
        {
            var folder = fileSystem.Path.GetDirectoryName(song.DestinationPath!);
            if (string.IsNullOrEmpty(folder))
                continue;

            if (!coverByFolder.TryGetValue(folder, out var hasCover))
            {
                hasCover = fileSystem.Directory.Exists(folder) && coverArtResolver.DirectoryHasCoverImage(folder);
                coverByFolder[folder] = hasCover;
            }

            if (hasCover)
            {
                song.HasCoverArt = true;
                flagged++;
            }
        }

        if (flagged > 0)
            await db.SaveChangesAsync(ct);
    }

    private sealed record FolderCandidate(string Folder, SweepRow Representative, ExternalCoverArtQuery Query);

    private sealed record SweepRow(
        int Id, Guid OwnerUserId, string SourcePath, string DestinationPath, bool SourceReleased,
        string? Album, string? AlbumArtist, string? MusicBrainzReleaseId, string? MusicBrainzReleaseGroupId,
        string? SpotifyId);
}
