using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment;

/// <summary>
/// Background sweep that keeps every song's lyric timing verdict current, in two passes with very different
/// costs.
///
/// The <b>free pass</b> runs the arithmetic checks over any song that has never been judged. It touches no
/// API, so it is allowed to chew through the entire library as fast as the database will serve it — which is
/// how a pre-existing collection gets verdicts without a migration-time backfill.
///
/// The <b>probe pass</b> spends real transcription quota, so it only ever looks at songs the free pass has
/// already flagged as Suspect, it is bounded per batch, and every window is reserved from
/// <see cref="LyricsProbeBudget"/> first. A song that has burned its probe attempts is left alone: the point
/// of the budget is that an unfixable track cannot quietly consume the allowance the fixable ones need.
/// </summary>
public sealed class LyricsTimingCheckService(
    IServiceScopeFactory scopeFactory,
    LyricsTimingProbe probe,
    IOptionsMonitor<LyricsTimingOptions> options,
    ILogger<LyricsTimingCheckService> logger) : BackgroundService
{
    /// <summary>
    /// When the probe pass may next spend quota. A probe that comes back empty-handed means the provider is
    /// refusing us or the budget is gone, and neither clears in the time it takes to run one more database
    /// batch — without this the free pass (which loops as fast as the DB will serve it) would drag a
    /// pointless, rate-limit-blocked probe attempt behind every single iteration.
    /// </summary>
    private DateTime _probeDeferredUntilUtc = DateTime.MinValue;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Lyrics timing check service started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;
            try
            {
                worked = await RunFreePassAsync(stoppingToken);
                worked |= await RunProbePassAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Lyrics timing check sweep failed.");
            }

            if (worked)
                continue;

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.CurrentValue.SweepIdleSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Judges a batch of never-checked songs with the free arithmetic checks. Returns true when it did work,
    /// so the caller comes straight back for the next batch instead of sleeping.
    /// </summary>
    private async Task<bool> RunFreePassAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        // A generous batch: this pass is pure arithmetic over text we already hold.
        var songs = await db.Songs
            .IgnoreQueryFilters()
            .WhereEligibleForLyricsTimingCheck()
            .Where(s => s.LyricsSyncStatus == LyricsSyncStatus.NotChecked)
            .OrderByDescending(s => s.Id)
            .Take(500)
            .ToListAsync(ct);

        if (songs.Count == 0)
            return false;

        var suspect = 0;
        foreach (var song in songs)
        {
            var verdict = LyricsTimingValidator.Check(song);
            song.ApplyLyricsSyncVerdict(verdict.Status, verdict.Issue);
            if (verdict.Status == LyricsSyncStatus.Suspect)
            {
                suspect++;
                logger.LogInformation(
                    "Lyrics timing looks wrong for {Track} (SongId={SongId}): {Issue}",
                    song.TrackLabel, song.Id, verdict.Issue);
            }
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Lyrics timing free check: judged {Count} song(s), {Suspect} suspect.", songs.Count, suspect);
        return true;
    }

    /// <summary>Probes a bounded batch of already-flagged songs, applying a repair when one is measurable.</summary>
    private async Task<bool> RunProbePassAsync(CancellationToken ct)
    {
        var opts = options.CurrentValue;
        if (!opts.EnableProbeSweep || !probe.IsAvailable)
            return false;

        if (DateTime.UtcNow < _probeDeferredUntilUtc)
            return false;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var songs = await db.Songs
            .IgnoreQueryFilters()
            .WhereEligibleForLyricsTimingCheck()
            .Where(s => s.LyricsSyncStatus == LyricsSyncStatus.Suspect)
            .Where(s => s.LyricsSyncProbeAttempts < opts.MaxProbeAttempts)
            .OrderBy(s => s.LyricsSyncProbeAttempts)
            .ThenByDescending(s => s.Id)
            .Take(opts.SweepBatchSize)
            .ToListAsync(ct);

        if (songs.Count == 0)
            return false;

        var probed = 0;
        foreach (var song in songs)
        {
            ct.ThrowIfCancellationRequested();

            var path = SongsEndpoints.ResolveAudioFilePath(song);
            if (path is null)
                continue;

            var result = await probe.ProbeAsync(song, path, ct);
            if (result is null)
            {
                // Out of budget, or the provider is rate-limiting us. Either way nothing was learned about
                // this song, so stop the batch and stand down for a while rather than queueing behind a
                // limit that has not moved.
                _probeDeferredUntilUtc = DateTime.UtcNow.AddMinutes(1);
                break;
            }

            song.RecordLyricsSyncProbeAttempt();
            ApplyProbeResult(song, result, opts);
            probed++;
        }

        if (probed > 0)
            await db.SaveChangesAsync(ct);

        return probed > 0;
    }

    /// <summary>
    /// Writes a probe verdict onto the song, repairing the LRC when the drift turned out to be one constant
    /// offset. Shared with the on-demand endpoint so the button and the sweep can never disagree.
    /// </summary>
    internal static void ApplyProbeResult(SongMetadata song, LyricsProbeResult result, LyricsTimingOptions opts)
    {
        if (result.Status == LyricsSyncStatus.Corrected)
        {
            var shifted = LyricsTimingValidator.ShiftLrc(song.SyncedLyrics, result.OffsetSeconds);
            if (shifted is not null)
            {
                song.ApplyLyricsSyncOffset(shifted, (int)Math.Round(result.OffsetSeconds * 1000), result.Confidence);
                return;
            }

            // Nothing to shift after all — record it honestly rather than claiming a repair.
            song.ApplyLyricsSyncVerdict(
                LyricsSyncStatus.Unverifiable, "the lyrics could not be re-timed", result.Confidence);
            return;
        }

        song.ApplyLyricsSyncVerdict(result.Status, result.Issue, result.Confidence);
    }
}

public static class LyricsTimingQueries
{
    /// <summary>
    /// Songs whose LRC timing can meaningfully be judged: a real, live, non-demo row that actually holds
    /// synced lyrics. Everything else has no timestamps to be wrong about.
    /// </summary>
    public static IQueryable<SongMetadata> WhereEligibleForLyricsTimingCheck(this IQueryable<SongMetadata> query)
        => query
            .Where(s => s.DeletedAtUtc == null)
            .Where(s => !s.IsSynthetic)
            .ExcludingDemoTenant()
            .Where(s => s.SyncedLyrics != null && s.SyncedLyrics != string.Empty);
}
