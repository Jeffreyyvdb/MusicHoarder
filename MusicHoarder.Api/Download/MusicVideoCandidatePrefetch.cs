using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

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
/// Only on an instance that has fetched at least one music video, so one that never uses them pays
/// no YouTube traffic for this. Only an admin's songs: the picker is admin-only, so searching for anyone else's would be traffic
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
    /// <summary>Consecutive failed searches that end a pass: yt-dlp is missing, or YouTube is refusing.</summary>
    private const int MaxConsecutiveFailures = 3;

    /// <summary>
    /// What deciding whether a song is due needs. The pass looks at the whole library, so it loads
    /// these few columns, not the entities; only the batch it searches is loaded in full.
    /// </summary>
    private sealed record SongRow(int Id, string? Artist, string? Title, int? DurationMs, int? DurationSeconds);

    private sealed record SetRow(int SongId, string QueryKey, DateTime? NextSearchAtUtc);

    /// <returns>How many songs got a list this pass.</returns>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        // Only where music videos are used: an instance that has never fetched one gets no YouTube
        // traffic from this.
        if (!await db.SongMusicVideos.IgnoreQueryFilters().AnyAsync(ct))
            return 0;

        var opts = options.Value;
        var now = time.GetUtcNow().UtcDateTime;

        // IgnoreQueryFilters throughout: a background scope has no caller, and the admin restriction
        // below is the scope wanted.
        var adminIds = db.Users.IgnoreQueryFilters()
            .Where(u => u.Role == UserRole.Admin)
            .Select(u => u.Id);

        // Newest first: a song just added is searched before the back catalogue. A song with no
        // acquisition stamp is ordered by when it was indexed.
        var songs = await db.Songs.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.DeletedAtUtc == null
                && adminIds.Contains(s.OwnerUserId)
                && s.EnrichmentStatus != EnrichmentStatus.Pending
                && s.Title != null && s.Title != ""
                // A video that failed is exactly when a list of alternatives is wanted.
                && !db.SongMusicVideos.IgnoreQueryFilters()
                    .Any(v => v.SongId == s.Id && v.Status != MusicVideoStatus.Failed))
            .OrderByDescending(s => s.AcquiredAtUtc ?? s.IndexedAtUtc)
            .ThenByDescending(s => s.Id)
            .Select(s => new SongRow(s.Id, s.Artist, s.Title, s.DurationMs, s.DurationSeconds))
            .ToListAsync(ct);
        if (songs.Count == 0)
            return 0;

        var sets = await db.SongVideoCandidateSets.IgnoreQueryFilters().AsNoTracking()
            .Select(c => new SetRow(c.SongId, c.QueryKey, c.NextSearchAtUtc))
            .ToDictionaryAsync(c => c.SongId, ct);

        var dueIds = songs
            .Where(song => IsDue(song, sets.GetValueOrDefault(song.Id), now))
            .Take(opts.MusicVideoCandidatePrefetchBatchSize)
            .Select(song => song.Id)
            .ToList();
        if (dueIds.Count == 0)
            return 0;

        var byId = (await db.Songs.IgnoreQueryFilters().AsNoTracking()
            .Where(s => dueIds.Contains(s.Id))
            .ToListAsync(ct)).ToDictionary(s => s.Id);
        var due = dueIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();

        var currentIds = await db.SongMusicVideos.IgnoreQueryFilters().AsNoTracking()
            .Where(v => dueIds.Contains(v.SongId) && v.YouTubeVideoId != null)
            .ToDictionaryAsync(v => v.SongId, v => v.YouTubeVideoId, ct);

        var stored = 0;
        var failures = 0;
        foreach (var song in due)
        {
            Exception? error = null;
            try
            {
                var list = await cache.SearchAsync(
                    db, song, currentIds.GetValueOrDefault(song.Id), probeTop: true, ct);
                if (list.Candidates.Count > 0)
                {
                    stored++;
                    failures = 0;
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

        if (stored > 0)
            logger.LogInformation("Prefetched music video candidates for {Count} song(s)", stored);
        return stored;
    }

    /// <summary>No list yet, the schedule has come round, or the song's search changed since the list was made.</summary>
    private static bool IsDue(SongRow song, SetRow? set, DateTime now) =>
        set is null
        || set.NextSearchAtUtc is not { } next
        || next <= now
        || set.QueryKey != MusicVideoCandidateCache.QueryKeyFor(song.Artist, song.Title, song.DurationMs, song.DurationSeconds);
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
    ILogger<MusicVideoCandidatePrefetchService> logger) : BackgroundService
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
        if (!await DelayAsync(TimeSpan.FromMinutes(2), stoppingToken)) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            var passStarted = Stopwatch.StartNew();
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
