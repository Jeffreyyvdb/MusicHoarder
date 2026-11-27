using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.BackgroundWork;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Settings;
using MusicHoarder.Api.Sync;

namespace MusicHoarder.Api.Download;

/// <summary>
/// One pass of the music-video candidate prefetch: for the library's songs that have no video yet —
/// My tracks first, then other built library tracks, then unbuilt rows, newest first within each —
/// search YouTube and probe the leading candidates, and remember the answer
/// (<see cref="MusicVideoCandidateCache"/>). Opening "Choose video…" on a song then shows a measured
/// list at once instead of waiting on the search.
///
/// <para>
/// Every live, non-duplicate song of an admin is in scope, however old: a new single's video can come
/// out long after the song was added. What keeps that bounded is the schedule — a song is searched
/// again only when its set's <c>NextSearchAtUtc</c> says so (<see cref="MusicVideoSearchSchedule"/>),
/// or its search changed (a rename, a ranking bump).
/// </para>
///
/// <para>
/// Only on an instance that has fetched at least one music video or turned on automatic downloads,
/// so one that never uses them pays no YouTube traffic for this. Only an admin's songs: the picker is
/// admin-only, so searching for anyone else's would be traffic no one can use. A song waits until
/// enrichment has finished with it, because the search is by artist and title and enrichment is what
/// corrects them — a list searched under the file's raw tags would be thrown away as soon as the song
/// was renamed.
/// </para>
///
/// <para>
/// With <c>AutoDownloadMusicVideos</c> on, a search that leaves a candidate passing every gate of
/// <see cref="MusicVideoAutoPick"/> also queues that video for download, and already-stored lists
/// holding a confident pick (for instance from searches that ran before automatic downloads were
/// switched on) are drained in the same priority order — unless the song already has a video row (a
/// failed pick waits for a person) or the disk is short. Otherwise nothing is downloaded.
/// </para>
/// </summary>
public sealed class MusicVideoCandidatePrefetch(
    MusicHoarderDbContext db,
    MusicVideoCandidateCache cache,
    MusicVideoChannel channel,
    IMusicVideoDownloader downloader,
    IFreeDiskSpace disk,
    IRuntimeSettingsService settings,
    IOptions<MusicEnricherOptions> options,
    IOptions<SyncOptions> syncOptions,
    MusicVideoOutcomeMemo memo,
    TimeProvider time,
    ILogger<MusicVideoCandidatePrefetch> logger)
{
    /// <summary>Consecutive failed searches that end a pass: yt-dlp is missing, or YouTube is refusing.</summary>
    private const int MaxConsecutiveFailures = 3;

    /// <summary>Stored candidate lists loaded per query while draining waiting confident picks.</summary>
    private const int StoredPickChunkSize = 200;

    /// <returns>How many songs got a list this pass.</returns>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var autoDownload = (await settings.GetAsync(ct)).AutoDownloadMusicVideos;
        if (!await MusicVideoPrefetchScope.HasStartedAsync(db, autoDownload, ct))
            return 0;

        var opts = options.Value;
        var syncedDir = syncOptions.Value.SyncedSourceDirectory;
        var now = time.GetUtcNow().UtcDateTime;

        // The pass looks at the whole library, so it loads these few columns, not the entities;
        // only the batch it searches is loaded in full.
        var rawSongs = await MusicVideoPrefetchScope.InPrefetchScope(db)
            .InPrefetchOrder()
            .Select(s => new
            {
                s.Id,
                s.OwnerUserId,
                s.Artist,
                s.Title,
                s.DurationMs,
                s.DurationSeconds,
                s.SourcePath,
                s.SpotifyId,
                s.LikedAtUtc,
                s.AcquisitionIntent,
                s.LibraryBuildStatus,
                s.DestinationPath,
                s.EnrichmentStatus,
                AddedAt = s.AcquiredAtUtc ?? s.IndexedAtUtc,
            })
            .ToListAsync(ct);
        if (rawSongs.Count == 0)
            return 0;

        var saveDates = await SpotifySaveDates.LoadAsync(db, ct, allTenants: true);
        var songs = MusicVideoPrefetchScope.OrderByPriority(
            rawSongs.Select(s => new PrefetchSong(
                s.Id, s.Artist, s.Title, s.DurationMs, s.DurationSeconds,
                MusicVideoPrefetchScope.PriorityOf(
                    s.OwnerUserId, s.Id, s.SourcePath, s.SpotifyId, s.LikedAtUtc, s.AcquisitionIntent,
                    s.LibraryBuildStatus, s.DestinationPath, s.EnrichmentStatus,
                    saveDates, opts.DownloadDirectory, syncedDir),
                s.AddedAt)),
            s => s.Priority,
            s => s.AddedAt,
            s => s.Id).ToList();

        var sets = await db.SongVideoCandidateSets.IgnoreQueryFilters().AsNoTracking()
            .Select(c => new PrefetchSet(
                c.SongId, c.QueryKey, c.NextSearchAtUtc, c.SearchedAtUtc,
                c.Error != null, c.EmptySearches))
            .ToDictionaryAsync(c => c.SongId, ct);

        // Only Failed rows can exist for songs in InPrefetchScope (Ready and Fetching are excluded);
        // they name the current video for picker searches and are never downloaded automatically.
        var failedVideos = await db.SongMusicVideos.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.Status == MusicVideoStatus.Failed)
            .ToDictionaryAsync(v => v.SongId, v => v.YouTubeVideoId, ct);

        var searchBudget = opts.MusicVideoCandidatePrefetchBatchSize;
        var downloadSlots = 0;
        var budget = new DiskBudget();
        var gates = MusicVideoPickGates.From(opts);

        if (autoDownload)
        {
            var fetching = await db.SongMusicVideos.IgnoreQueryFilters().AsNoTracking()
                .Where(v => v.Status == MusicVideoStatus.Fetching && v.Song.DeletedAtUtc == null)
                .Select(v => new FetchingRow(v.SongId, v.Pick, v.YouTubeVideoId))
                .ToListAsync(ct);
            var fetchingIds = fetching.Select(f => f.SongId).ToHashSet();
            await SeedMissingReservationsAsync(fetching, opts, ct);
            channel.RetainReservations(fetchingIds);
            budget.QueuedBytes = channel.ReservedBytes;
            downloadSlots = Math.Max(0, opts.MusicVideoCandidatePrefetchBatchSize - fetchingIds.Count);
        }

        // Pre-load full SongMetadata only for the due songs this pass can actually search.
        // The first due song past searchBudget sets the frontier: nothing after it may drain a
        // stored pick ahead of an unsearched higher-priority (or newer) song.
        var dueBatchIds = new List<int>(Math.Max(0, searchBudget));
        var searchFrontierIndex = songs.Count;
        for (var i = 0; i < songs.Count; i++)
        {
            var song = songs[i];
            if (!MusicVideoPrefetchScope.IsDue(song, sets.GetValueOrDefault(song.Id), now))
                continue;
            if (dueBatchIds.Count < searchBudget)
                dueBatchIds.Add(song.Id);
            else
            {
                searchFrontierIndex = i;
                break;
            }
        }

        var dueById = dueBatchIds.Count == 0
            ? new Dictionary<int, SongMetadata>()
            : (await db.Songs.IgnoreQueryFilters().AsNoTracking()
                .Where(s => dueBatchIds.Contains(s.Id))
                .ToListAsync(ct)).ToDictionary(s => s.Id);

        // Pre-check disk headroom once before reading any stored CandidatesJson on a short drive.
        var floor = opts.MusicVideoAutoDownloadMinFreeGb * 1024L * 1024L * 1024L;
        if (autoDownload && downloadSlots > 0)
        {
            var freeNow = disk.AvailableBytes(downloader.ResolveVideoDirectory());
            if (freeNow is not { } availableNow || availableNow - budget.QueuedBytes < floor)
                LogDiskShortnessOnce(budget, freeNow, floor);
        }

        // Lazily chunk-loaded CandidatesJson for non-due stored picks ahead of the search frontier.
        var loadedJson = new Dictionary<int, (string Json, int Rewrites)>();
        var nextJsonScanIndex = 0;

        async Task EnsureJsonLoadedAsync(int fromIndex)
        {
            if (loadedJson.ContainsKey(songs[fromIndex].Id))
                return;
            var chunk = new List<(int SongId, int Rewrites)>(StoredPickChunkSize);
            var scan = Math.Max(fromIndex, nextJsonScanIndex);
            for (; scan < searchFrontierIndex && chunk.Count < StoredPickChunkSize; scan++)
            {
                var candidate = songs[scan];
                if (!IsDrainableCandidate(candidate, sets.GetValueOrDefault(candidate.Id), failedVideos, now, gates))
                    continue;
                chunk.Add((candidate.Id, memo.RewritesOf(candidate.Id)));
            }
            nextJsonScanIndex = scan;
            if (chunk.Count == 0)
                return;
            var chunkIds = chunk.Select(c => c.SongId).ToList();
            var rewritesById = chunk.ToDictionary(c => c.SongId, c => c.Rewrites);
            var rows = await db.SongVideoCandidateSets.IgnoreQueryFilters().AsNoTracking()
                .Where(c => chunkIds.Contains(c.SongId))
                .Select(c => new { c.SongId, c.CandidatesJson })
                .ToListAsync(ct);
            foreach (var row in rows)
                loadedJson[row.SongId] = (row.CandidatesJson, rewritesById[row.SongId]);
        }

        var searched = 0;
        var stored = 0;
        var queued = 0;
        var failures = 0;

        // Walk the single priority-ordered sequence once: My tracks first, then other built tracks,
        // then unbuilt rows, newest first within each. Fresh searches and stored confident picks
        // follow that one order and share the same download-slot and disk budgets.
        for (var i = 0; i < searchFrontierIndex; i++)
        {
            var song = songs[i];
            var set = sets.GetValueOrDefault(song.Id);
            var isDue = MusicVideoPrefetchScope.IsDue(song, set, now);

            if (isDue)
            {
                if (searchBudget <= 0)
                    break;
                if (!dueById.TryGetValue(song.Id, out var entity))
                    continue;

                searchBudget--;
                searched++;
                Exception? error = null;
                try
                {
                    var list = await cache.SearchAsync(
                        db, entity, failedVideos.GetValueOrDefault(song.Id), probeTop: true, ct);
                    if (list.Candidates.Count > 0)
                    {
                        stored++;
                        failures = 0;
                        if (autoDownload
                            && !failedVideos.ContainsKey(song.Id)
                            && downloadSlots > 0
                            && !budget.ShortnessLogged)
                        {
                            var outcome = await TryQueueAutomaticAsync(entity, list, opts, budget, ct);
                            if (outcome == QueueOutcome.Queued)
                            {
                                downloadSlots--;
                                queued++;
                            }
                            else if (outcome == QueueOutcome.CapacityFull)
                            {
                                downloadSlots = 0;
                            }
                        }
                        continue;
                    }
                    // Recorded by the cache; counted here, since a refused search reads as an empty one.
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    error = ex;
                    await cache.RecordFailureAsync(db, entity, ex.Message, ct);
                }

                if (++failures >= MaxConsecutiveFailures)
                {
                    logger.LogWarning(error,
                        "Music video candidate prefetch stopped after {Failures} failed or empty searches in a row (last: '{Artist} - {Title}')",
                        failures, LogSanitizer.ForLog(entity.Artist), LogSanitizer.ForLog(entity.Title));
                    break;
                }
            }
            else if (autoDownload && downloadSlots > 0 && !budget.ShortnessLogged
                && IsDrainableCandidate(song, set, failedVideos, now, gates))
            {
                await EnsureJsonLoadedAsync(i);
                var hasRow = loadedJson.TryGetValue(song.Id, out var entry);
                var list = hasRow ? MusicVideoCandidateCache.TryParse(entry.Json) ?? [] : [];
                var (pick, verdict) = MusicVideoAutoPick.Evaluate(
                    list, song.Artist, song.DurationMs, song.DurationSeconds, gates);
                if (hasRow)
                {
                    var key = new MusicVideoVerdictKey(
                        set!.SearchedAtUtc, gates, song.Artist, song.DurationMs, song.DurationSeconds);
                    memo.Remember(song.Id, entry.Rewrites, key, verdict);
                }

                if (pick is { EstimatedBytes: { } })
                {
                    var outcome = await TryQueuePickAsync(song.Id, pick, opts, budget, ct);
                    if (outcome == QueueOutcome.Queued)
                    {
                        downloadSlots--;
                        queued++;
                    }
                    else if (outcome == QueueOutcome.CapacityFull)
                    {
                        downloadSlots = 0;
                    }
                }
            }

            if (searchBudget <= 0 && (!autoDownload || downloadSlots <= 0 || budget.ShortnessLogged))
                break;
        }

        if (searched > 0 || queued > 0)
        {
            logger.LogInformation(
                "Music video candidate prefetch searched {Searched} song(s): {Stored} with candidates, {Queued} video(s) queued for download",
                searched, stored, queued);
        }
        return stored;
    }

    private bool IsDrainableCandidate(
        PrefetchSong song,
        PrefetchSet? set,
        IReadOnlyDictionary<int, string?> failedVideos,
        DateTime now,
        MusicVideoPickGates gates)
    {
        if (set is null)
            return false;
        if (!MusicVideoPrefetchScope.IsWaitingStoredPickCandidate(
            inScope: true,
            hasVideoRow: failedVideos.ContainsKey(song.Id),
            isDue: MusicVideoPrefetchScope.IsDue(song, set, now),
            hasError: set.HasError,
            emptySearches: set.EmptySearches))
            return false;
        var key = new MusicVideoVerdictKey(set.SearchedAtUtc, gates, song.Artist, song.DurationMs, song.DurationSeconds);
        return memo.Get(song.Id, key) is not { Outcome: not MusicVideoSearchOutcome.Clip };
    }

    private sealed record FetchingRow(int SongId, MusicVideoPick Pick, string? YouTubeVideoId);

    private enum QueueOutcome { NotQueued, Queued, CapacityFull }

    /// <summary>
    /// Recovers byte reservations for <see cref="MusicVideoStatus.Fetching"/> rows already in the
    /// database that this process did not queue (for example, right after a restart), looking up each
    /// row's pinned <see cref="SongMusicVideo.YouTubeVideoId"/> directly so a change in score gates
    /// never zeroes out a download that is still going to run. Falls back to the per-clip cap for an
    /// automatic fetch whose pinned estimate is unavailable.
    /// </summary>
    private async Task SeedMissingReservationsAsync(
        IReadOnlyList<FetchingRow> fetching, MusicEnricherOptions opts, CancellationToken ct)
    {
        var unreserved = fetching.Where(f => !channel.HasReservation(f.SongId)).ToList();
        if (unreserved.Count == 0)
            return;

        var ids = unreserved.Select(f => f.SongId).ToList();
        var jsonBySong = await db.SongVideoCandidateSets.IgnoreQueryFilters().AsNoTracking()
            .Where(c => ids.Contains(c.SongId))
            .Select(c => new { c.SongId, c.CandidatesJson })
            .ToDictionaryAsync(c => c.SongId, c => c.CandidatesJson, ct);

        var conservativeAutomaticFallback = opts.MusicVideoAutoDownloadMaxClipMb * 1024L * 1024L;
        foreach (var row in unreserved)
        {
            long? estimated = null;
            if (row.YouTubeVideoId is not null
                && jsonBySong.TryGetValue(row.SongId, out var text)
                && MusicVideoCandidateCache.TryParse(text) is { } candidates)
            {
                estimated = candidates
                    .FirstOrDefault(c => c.VideoId == row.YouTubeVideoId)
                    ?.EstimatedBytes;
            }

            if (estimated is > 0)
                channel.ReserveBytes(row.SongId, estimated.Value);
            else if (row.Pick == MusicVideoPick.Automatic && conservativeAutomaticFallback > 0)
                channel.ReserveBytes(row.SongId, conservativeAutomaticFallback);
        }
    }

    /// <summary>What a pass has committed to the videos drive so far, and whether it has said so.</summary>
    private sealed class DiskBudget
    {
        public long QueuedBytes { get; set; }
        public bool ShortnessLogged { get; set; }
    }

    /// <summary>Queues the list's confident pick, if it has one and the drive can take it.</summary>
    private Task<QueueOutcome> TryQueueAutomaticAsync(
        SongMetadata song, VideoCandidateList list, MusicEnricherOptions opts, DiskBudget budget, CancellationToken ct)
    {
        return MusicVideoAutoPick.Choose(list.Candidates, song, opts) is { EstimatedBytes: { } } pick
            ? TryQueuePickAsync(song.Id, pick, opts, budget, ct)
            : Task.FromResult(QueueOutcome.NotQueued);
    }

    private async Task<QueueOutcome> TryQueuePickAsync(
        int songId, CachedVideoCandidate pick, MusicEnricherOptions opts, DiskBudget budget, CancellationToken ct)
    {
        if (pick.EstimatedBytes is not { } clipBytes)
            return QueueOutcome.NotQueued;

        // Recheck live capacity right before admitting an automatic pick so a manual request that
        // arrived during a slow search cannot push live fetches past the cap.
        var liveFetching = await db.SongMusicVideos.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(v => v.Status == MusicVideoStatus.Fetching && v.Song.DeletedAtUtc == null, ct);
        if (liveFetching >= opts.MusicVideoCandidatePrefetchBatchSize)
            return QueueOutcome.CapacityFull;

        var free = disk.AvailableBytes(downloader.ResolveVideoDirectory());
        var floor = opts.MusicVideoAutoDownloadMinFreeGb * 1024L * 1024L * 1024L;
        if (free is not { } available || available - budget.QueuedBytes - clipBytes < floor)
        {
            LogDiskShortnessOnce(budget, free, floor);
            return QueueOutcome.NotQueued;
        }

        if (await MusicVideoFetchQueue.TryPickAsync(db, channel, songId, pick.VideoId, ct, clipBytes) is null)
            return QueueOutcome.NotQueued;

        budget.QueuedBytes += clipBytes;
        logger.LogInformation(
            "Queued automatic music video download for song {SongId}: video {VideoId} (score {Score})",
            songId, pick.VideoId, pick.Score);
        return QueueOutcome.Queued;
    }

    private void LogDiskShortnessOnce(DiskBudget budget, long? free, long floor)
    {
        if (budget.ShortnessLogged)
            return;
        budget.ShortnessLogged = true;
        logger.LogWarning(
            "Not downloading music videos automatically this pass: {Free} bytes free on the videos drive (unknown when blank), {Queued} already queued, floor {Floor} bytes",
            free, budget.QueuedBytes, floor);
    }
}

/// <summary>
/// Runs <see cref="MusicVideoCandidatePrefetch"/> every <c>MusicVideoCandidatePrefetchIntervalMinutes</c>
/// (0 disables it), counted from one pass's start to the next so a pass that searched for minutes is
/// not followed by a further full wait. A plain hosted service beside <see cref="MusicVideoBackgroundService"/>, never a
/// JobManager step: it is slow network side-work.
/// </summary>
public sealed class MusicVideoCandidatePrefetchService(
    IServiceScopeFactory scopes,
    IOptionsMonitor<MusicEnricherOptions> options,
    ILogger<MusicVideoCandidatePrefetchService> logger,
    BackgroundWorkStatus? status = null) : BackgroundService
{
    private static readonly TimeSpan MinimumPause = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.CurrentValue.MusicVideoCandidatePrefetchIntervalMinutes <= 0)
        {
            logger.LogInformation("Music video candidate prefetch disabled (MusicVideoCandidatePrefetchIntervalMinutes=0)");
            return;
        }

        // Let startup (migrations, the first scan) settle before searching.
        var settle = TimeSpan.FromMinutes(2);
        status?.Idle(BackgroundJobIds.MusicVideos, DateTime.UtcNow + settle);
        if (!await DelayAsync(settle, stoppingToken)) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            var passStarted = Stopwatch.StartNew();
            var minutes = Math.Clamp(options.CurrentValue.MusicVideoCandidatePrefetchIntervalMinutes, 1, 1440);
            var next = DateTime.UtcNow.AddMinutes(minutes);
            status?.Working(BackgroundJobIds.MusicVideos);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var prefetch = scope.ServiceProvider.GetRequiredService<MusicVideoCandidatePrefetch>();
                var stored = await prefetch.RunAsync(stoppingToken);
                status?.Idle(BackgroundJobIds.MusicVideos, next, stored > 0);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Music video candidate prefetch failed");
                status?.Failing(BackgroundJobIds.MusicVideos, ex, next);
            }

            // Never a hot loop: a pass that overran its interval still pauses before the next.
            var wait = TimeSpan.FromMinutes(minutes) - passStarted.Elapsed;
            if (!await DelayAsync(wait > MinimumPause ? wait : MinimumPause, stoppingToken)) return;
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
