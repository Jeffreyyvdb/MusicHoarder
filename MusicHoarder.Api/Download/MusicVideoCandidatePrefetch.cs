using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.BackgroundWork;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Settings;

namespace MusicHoarder.Api.Download;

/// <summary>
/// One pass of the music-video candidate prefetch: for the library's songs that have no video yet,
/// newest first, search YouTube and probe the leading candidates, and remember the answer
/// (<see cref="MusicVideoCandidateCache"/>). Opening "Choose video…" on a song then shows a measured
/// list at once instead of waiting on the search.
///
/// <para>
/// Every live song of an admin is in scope, however old: a new single's video can come out long after
/// the song was added. What keeps that bounded is the schedule — a song is searched again only when
/// its set's <c>NextSearchAtUtc</c> says so (<see cref="MusicVideoSearchSchedule"/>), or its search
/// changed (a rename, a ranking bump).
/// </para>
///
/// <para>
/// Only on an instance that has fetched at least one music video or turned on automatic downloads,
/// so one that never uses them pays no YouTube traffic for this. Only an admin's songs: the picker is admin-only, so searching for anyone else's would be traffic
/// no one can use. A song waits until enrichment has finished with it, because the search is by artist
/// and title and enrichment is what corrects them — a list searched under the file's raw tags would be
/// thrown away as soon as the song was renamed.
/// </para>
///
/// <para>
/// With <c>AutoDownloadMusicVideos</c> on, a search that leaves a candidate passing every gate of
/// <see cref="MusicVideoAutoPick"/> also queues that video for download, unless the song already has a
/// video row (a failed pick waits for a person) or the disk is short. Otherwise nothing is downloaded.
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
    TimeProvider time,
    ILogger<MusicVideoCandidatePrefetch> logger)
{
    /// <summary>Consecutive failed searches that end a pass: yt-dlp is missing, or YouTube is refusing.</summary>
    private const int MaxConsecutiveFailures = 3;

    /// <returns>How many songs got a list this pass.</returns>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        // Only where music videos are used: an instance that has never fetched one, and has not asked
        // for them to be downloaded automatically, gets no YouTube traffic from this.
        var autoDownload = (await settings.GetAsync(ct)).AutoDownloadMusicVideos;
        if (!autoDownload && !await db.SongMusicVideos.IgnoreQueryFilters().AnyAsync(ct))
            return 0;

        var opts = options.Value;
        var now = time.GetUtcNow().UtcDateTime;

        // The pass looks at the whole library, so it loads these few columns, not the entities;
        // only the batch it searches is loaded in full.
        var songs = await db.Songs.IgnoreQueryFilters().AsNoTracking()
            .InPrefetchScope(db)
            .InPrefetchOrder()
            .Select(s => new PrefetchSong(s.Id, s.Artist, s.Title, s.DurationMs, s.DurationSeconds))
            .ToListAsync(ct);
        if (songs.Count == 0)
            return 0;

        var sets = await db.SongVideoCandidateSets.IgnoreQueryFilters().AsNoTracking()
            .Select(c => new PrefetchSet(c.SongId, c.QueryKey, c.NextSearchAtUtc))
            .ToDictionaryAsync(c => c.SongId, ct);

        var dueIds = songs
            .Where(song => MusicVideoPrefetchScope.IsDue(song, sets.GetValueOrDefault(song.Id), now))
            .Take(opts.MusicVideoCandidatePrefetchBatchSize)
            .Select(song => song.Id)
            .ToList();
        if (dueIds.Count == 0)
            return 0;

        var byId = (await db.Songs.IgnoreQueryFilters().AsNoTracking()
            .Where(s => dueIds.Contains(s.Id))
            .ToListAsync(ct)).ToDictionary(s => s.Id);
        var due = dueIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();

        // The batch's video rows, failed ones too: they name the current video, and a song with any row
        // is never downloaded automatically.
        var videoRows = await db.SongMusicVideos.IgnoreQueryFilters().AsNoTracking()
            .Where(v => dueIds.Contains(v.SongId))
            .ToDictionaryAsync(v => v.SongId, v => v.YouTubeVideoId, ct);

        var budget = new DiskBudget();

        var searched = 0;
        var stored = 0;
        var queued = 0;
        var failures = 0;
        foreach (var song in due)
        {
            searched++;
            Exception? error = null;
            try
            {
                var list = await cache.SearchAsync(
                    db, song, videoRows.GetValueOrDefault(song.Id), probeTop: true, ct);
                if (list.Candidates.Count > 0)
                {
                    stored++;
                    failures = 0;
                    if (autoDownload && !videoRows.ContainsKey(song.Id)
                        && await TryQueueAutomaticAsync(song, list, budget, ct))
                        queued++;
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
                await cache.RecordFailureAsync(db, song, ex.Message, ct);
            }

            if (++failures >= MaxConsecutiveFailures)
            {
                logger.LogWarning(error,
                    "Music video candidate prefetch stopped after {Failures} failed or empty searches in a row (last: '{Artist} - {Title}')",
                    failures, LogSanitizer.ForLog(song.Artist), LogSanitizer.ForLog(song.Title));
                break;
            }
        }

        logger.LogInformation(
            "Music video candidate prefetch searched {Searched} song(s): {Stored} with candidates, {Queued} video(s) queued for download",
            searched, stored, queued);
        return stored;
    }

    /// <summary>What a pass has committed to the videos drive so far, and whether it has said so.</summary>
    private sealed class DiskBudget
    {
        public long QueuedBytes { get; set; }
        public bool ShortnessLogged { get; set; }
    }

    /// <summary>Queues the list's confident pick, if it has one and the drive can take it.</summary>
    private async Task<bool> TryQueueAutomaticAsync(
        SongMetadata song, VideoCandidateList list, DiskBudget budget, CancellationToken ct)
    {
        var opts = options.Value;
        if (MusicVideoAutoPick.Choose(list.Candidates, song, opts) is not { EstimatedBytes: { } clipBytes } pick)
            return false;

        var free = disk.AvailableBytes(downloader.ResolveVideoDirectory());
        var floor = opts.MusicVideoAutoDownloadMinFreeGb * 1024L * 1024L * 1024L;
        if (free is not { } available || available - budget.QueuedBytes - clipBytes < floor)
        {
            if (!budget.ShortnessLogged)
            {
                budget.ShortnessLogged = true;
                logger.LogWarning(
                    "Not downloading music videos automatically this pass: {Free} bytes free on the videos drive (unknown when blank), {Queued} already queued, floor {Floor} bytes",
                    free, budget.QueuedBytes, floor);
            }
            return false;
        }

        if (await MusicVideoFetchQueue.TryPickAsync(db, channel, song.Id, pick.VideoId, ct) is null)
            return false;

        budget.QueuedBytes += clipBytes;
        logger.LogInformation(
            "Queued automatic music video download for song {SongId}: video {VideoId} (score {Score})",
            song.Id, pick.VideoId, pick.Score);
        return true;
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
