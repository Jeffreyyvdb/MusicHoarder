using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Download;

/// <summary>
/// One pass of the music-video candidate prefetch: for songs added in the last
/// <c>MusicVideoCandidatePrefetchDays</c> that have no video yet, search YouTube and probe the leading
/// candidates, and remember the answer (<see cref="MusicVideoCandidateCache"/>). Opening "Choose
/// video…" on a new song then shows a measured list at once instead of waiting on the search.
///
/// <para>
/// Only an admin's songs: the picker is admin-only, so searching for anyone else's would be traffic
/// no one can use. A song waits until enrichment has finished with it, because the search is by artist
/// and title and enrichment is what corrects them — a list searched under the file's raw tags would be
/// thrown away as soon as the song was renamed. Nothing is downloaded here.
/// </para>
/// </summary>
public sealed class MusicVideoCandidatePrefetch(
    MusicHoarderDbContext db,
    MusicVideoCandidateCache cache,
    IOptions<MusicEnricherOptions> options,
    TimeProvider time,
    ILogger<MusicVideoCandidatePrefetch> logger)
{
    /// <summary>How long a failed search waits before the prefetch tries that song again.</summary>
    private static readonly TimeSpan RetryFailedAfter = TimeSpan.FromDays(1);

    /// <summary>Consecutive failed searches that end a pass: yt-dlp is missing, or YouTube is refusing.</summary>
    private const int MaxConsecutiveFailures = 3;

    /// <returns>How many songs got a list this pass.</returns>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var opts = options.Value;
        var now = time.GetUtcNow().UtcDateTime;
        var since = now.AddDays(-opts.MusicVideoCandidatePrefetchDays);

        // IgnoreQueryFilters throughout: a background scope has no caller, and the admin restriction
        // below is the scope wanted.
        var adminIds = db.Users.IgnoreQueryFilters()
            .Where(u => u.Role == UserRole.Admin)
            .Select(u => u.Id);

        var recent = await db.Songs.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.DeletedAtUtc == null
                && s.AcquiredAtUtc >= since
                && adminIds.Contains(s.OwnerUserId)
                && s.EnrichmentStatus != EnrichmentStatus.Pending
                && s.Title != null && s.Title != ""
                // A video that failed is exactly when a list of alternatives is wanted.
                && !db.SongMusicVideos.IgnoreQueryFilters()
                    .Any(v => v.SongId == s.Id && v.Status != MusicVideoStatus.Failed))
            .OrderByDescending(s => s.AcquiredAtUtc)
            .ToListAsync(ct);
        if (recent.Count == 0)
            return 0;

        var ids = recent.Select(s => s.Id).ToList();
        var sets = await db.SongVideoCandidateSets.IgnoreQueryFilters().AsNoTracking()
            .Where(c => ids.Contains(c.SongId))
            .ToDictionaryAsync(c => c.SongId, ct);

        var due = recent
            .Where(song => !sets.TryGetValue(song.Id, out var set) || NeedsSearch(set, song, now))
            .Take(opts.MusicVideoCandidatePrefetchBatchSize)
            .ToList();
        if (due.Count == 0)
            return 0;

        var currentIds = await db.SongMusicVideos.IgnoreQueryFilters().AsNoTracking()
            .Where(v => ids.Contains(v.SongId) && v.YouTubeVideoId != null)
            .ToDictionaryAsync(v => v.SongId, v => v.YouTubeVideoId, ct);

        var stored = 0;
        var failures = 0;
        foreach (var song in due)
        {
            try
            {
                await cache.SearchAsync(db, song, currentIds.GetValueOrDefault(song.Id), probeTop: true, ct);
                stored++;
                failures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await cache.RecordFailureAsync(db, song, ex.Message, ct);
                if (++failures >= MaxConsecutiveFailures)
                {
                    logger.LogWarning(ex,
                        "Music video candidate prefetch stopped after {Failures} failed searches in a row (last: '{Artist} - {Title}')",
                        failures, LogSanitizer.ForLog(song.Artist), LogSanitizer.ForLog(song.Title));
                    break;
                }
            }
        }

        if (stored > 0)
            logger.LogInformation("Prefetched music video candidates for {Count} new song(s)", stored);
        return stored;
    }

    private bool NeedsSearch(SongVideoCandidateSet set, SongMetadata song, DateTime now)
    {
        if (set.Error is not null)
            return now - set.SearchedAtUtc >= RetryFailedAfter;
        return !cache.IsServable(set, MusicVideoCandidateCache.QueryKeyFor(song));
    }
}

/// <summary>
/// Runs <see cref="MusicVideoCandidatePrefetch"/> every <c>MusicVideoCandidatePrefetchIntervalMinutes</c>
/// (0 disables it). A plain hosted service beside <see cref="MusicVideoBackgroundService"/>, never a
/// JobManager step: it is slow network side-work.
/// </summary>
public sealed class MusicVideoCandidatePrefetchService(
    IServiceScopeFactory scopes,
    IOptionsMonitor<MusicEnricherOptions> options,
    ILogger<MusicVideoCandidatePrefetchService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.CurrentValue.MusicVideoCandidatePrefetchIntervalMinutes <= 0)
        {
            logger.LogInformation("Music video candidate prefetch disabled (MusicVideoCandidatePrefetchIntervalMinutes=0)");
            return;
        }

        // Let startup (migrations, the first scan) settle before searching.
        if (!await DelayAsync(TimeSpan.FromMinutes(2), stoppingToken)) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var prefetch = scope.ServiceProvider.GetRequiredService<MusicVideoCandidatePrefetch>();
                await prefetch.RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Music video candidate prefetch failed");
            }

            var minutes = Math.Clamp(options.CurrentValue.MusicVideoCandidatePrefetchIntervalMinutes, 1, 1440);
            if (!await DelayAsync(TimeSpan.FromMinutes(minutes), stoppingToken)) return;
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
