using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment;

/// <summary>
/// Background sweep that keeps every song's lyric timing verdict current, and fixes what it can, in three
/// passes with very different costs.
///
/// The <b>free pass</b> runs the arithmetic checks over any song that has never been judged. It touches no
/// API, so it is allowed to chew through the entire library as fast as the database will serve it — which is
/// how a pre-existing collection gets verdicts without a migration-time backfill.
///
/// The <b>probe pass</b> spends real transcription quota, so it is bounded per batch and every window is
/// reserved from <see cref="LyricsProbeBudget"/> first. Songs the free pass flagged as Suspect go first; after
/// them come songs the free checks passed but no probe has heard, because those checks only catch gross
/// mistakes and a sloppily timed LRC sails through them. A song that has burned its probe attempts is left
/// alone: the point of the budget is that an unfixable track cannot quietly consume the allowance the fixable
/// ones need.
///
/// The <b>re-sync pass</b> acts on what the probe found. A probe that measures one constant offset repairs the
/// LRC on the spot; one that finds the timing drifting leaves the song Suspect, and this pass then runs the
/// same full transcription and forced alignment as the "Sync with AI" button. It costs a whole song's audio,
/// so it is capped per day and draws on the same budget.
/// </summary>
public sealed class LyricsTimingCheckService(
    IServiceScopeFactory scopeFactory,
    LyricsTimingProbe probe,
    ILyricsTranscriptionService transcriber,
    LyricsProbeBudget budget,
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

    /// <summary>The same, for the re-sync pass, which a closed budget or a busy provider stops just the same.</summary>
    private DateTime _resyncDeferredUntilUtc = DateTime.MinValue;

    /// <summary>
    /// When each automatic re-sync of the last day ran, for <see cref="LyricsTimingOptions.AutoResyncPerDay"/>.
    /// In memory, like the audio budget: a restart forgets it, and the worst that costs is one extra day's
    /// worth of re-syncs, which the audio budget still bounds.
    /// </summary>
    private readonly Queue<DateTime> _resyncsToday = new();

    /// <summary>A song with no known length is budgeted as this long, so it cannot slip past the audio meter.</summary>
    private const double UnknownDurationSeconds = 300;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Lyrics timing check service started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;
            try
            {
                worked = await RunFreePassAsync(stoppingToken);
                // Re-sync before probing: a song the last probe heard drifting is then re-synced, rather
                // than spending its second probe attempt on the same window first.
                worked |= await RunResyncPassAsync(stoppingToken);
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
    internal async Task<bool> RunFreePassAsync(CancellationToken ct)
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

    /// <summary>
    /// Probes a bounded batch of songs — flagged ones first, then ones only the free checks have vouched for —
    /// applying a repair when one is measurable.
    /// </summary>
    internal async Task<bool> RunProbePassAsync(CancellationToken ct)
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
            .WhereEligibleForLyricsTimingProbe()
            .Where(s => s.LyricsSyncStatus == LyricsSyncStatus.Suspect)
            .Where(s => s.LyricsSyncProbeAttempts < opts.MaxProbeAttempts)
            .OrderBy(s => s.LyricsSyncProbeAttempts)
            .ThenByDescending(s => s.Id)
            .Take(opts.SweepBatchSize)
            .ToListAsync(ct);

        if (songs.Count < opts.SweepBatchSize && opts.ProbeUnconfirmedSongs)
        {
            // "Ok" with no probe attempt means only the arithmetic has looked at it: nothing provably wrong,
            // which a uniformly shifted or carelessly timed LRC also satisfies. Newest first, so what was
            // just added is checked before the back catalogue.
            songs.AddRange(await db.Songs
                .IgnoreQueryFilters()
                .WhereEligibleForLyricsTimingProbe()
                .Where(s => s.LyricsSyncStatus == LyricsSyncStatus.Ok)
                .Where(s => s.LyricsSyncProbeAttempts == 0)
                .OrderByDescending(s => s.Id)
                .Take(opts.SweepBatchSize - songs.Count)
                .ToListAsync(ct));
        }

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
    /// Re-syncs songs a probe has heard drifting: a full transcription, forced-aligned to the song's own
    /// lyric text, which then becomes what the player shows. Bounded by the daily cap and the audio budget.
    /// </summary>
    internal async Task<bool> RunResyncPassAsync(CancellationToken ct)
    {
        var opts = options.CurrentValue;
        if (!opts.EnableAutoResync || !opts.EnableProbeSweep || !opts.EnableAiProbe || !transcriber.IsConfigured)
            return false;

        if (DateTime.UtcNow < _resyncDeferredUntilUtc)
            return false;

        var remaining = opts.AutoResyncPerDay - CountResyncsToday();
        if (remaining <= 0)
            return false;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var songs = await db.Songs
            .IgnoreQueryFilters()
            .WhereDueForAutoResync()
            .OrderByDescending(s => s.Id)
            // A whole song per call: a handful per pass keeps one pass from sitting on the rate limiter.
            .Take(Math.Min(remaining, 3))
            .ToListAsync(ct);

        if (songs.Count == 0)
            return false;

        var resynced = 0;
        foreach (var song in songs)
        {
            ct.ThrowIfCancellationRequested();

            var path = SongsEndpoints.ResolveAudioFilePath(song);
            if (path is null)
                continue;

            var seconds = song.DurationSeconds is > 0 ? song.DurationSeconds.Value : UnknownDurationSeconds;
            if (!budget.TryReserve(seconds))
            {
                _resyncDeferredUntilUtc = DateTime.UtcNow.AddMinutes(5);
                break;
            }

            try
            {
                var result = await transcriber.TranscribeAsync(song, path, ct);
                var applied = LyricsResync.Apply(song, result);
                logger.LogInformation(
                    "Lyrics auto re-sync for {Track} (SongId={SongId}): {Outcome}.",
                    song.TrackLabel,
                    song.Id,
                    applied.Promoted ? "official lyrics re-timed and now shown" : "transcribed, but the lyrics could not be aligned, so the LRCLIB version stays");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                budget.Refund(seconds);
                throw;
            }
            catch (TranscriptionRateLimitedException ex)
            {
                // We never got an answer, so nothing about the song is recorded and it stays due.
                budget.Refund(seconds);
                _resyncDeferredUntilUtc = DateTime.UtcNow.AddMinutes(1);
                logger.LogInformation("Lyrics auto re-sync deferred for SongId={SongId}: {Reason}", song.Id, ex.Message);
                break;
            }
            catch (Exception ex)
            {
                // Recorded as failed, which takes it out of the queue: a song that cannot be transcribed would
                // otherwise be retried on every pass. The button can still try it again.
                logger.LogWarning(ex, "Lyrics auto re-sync failed for SongId={SongId}.", song.Id);
                song.MarkTranscriptionFailed(ex.Message);
            }

            _resyncsToday.Enqueue(DateTime.UtcNow);
            resynced++;
        }

        if (resynced > 0)
            await db.SaveChangesAsync(ct);

        return resynced > 0;
    }

    private int CountResyncsToday()
    {
        var cutoff = DateTime.UtcNow.AddDays(-1);
        while (_resyncsToday.Count > 0 && _resyncsToday.Peek() <= cutoff)
            _resyncsToday.Dequeue();
        return _resyncsToday.Count;
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

    /// <summary>
    /// Songs whose timing the background probe may spend budget on: eligible, and still showing their LRCLIB
    /// lyrics. A song whose player already shows an AI-synced version would be paying to judge lyrics nobody
    /// sees.
    /// </summary>
    public static IQueryable<SongMetadata> WhereEligibleForLyricsTimingProbe(this IQueryable<SongMetadata> query)
        => query
            .WhereEligibleForLyricsTimingCheck()
            .Where(s => s.PreferredLyricsSource == PreferredLyricsSource.Lrclib);

    /// <summary>
    /// Songs the re-sync pass should transcribe: a probe heard the timing drift (a probe only leaves a song
    /// Suspect when no single shift fixes it), the song has official lyric text to align to, and it has never
    /// been transcribed — so a transcription someone asked for, kept or declined is never overwritten, and a
    /// failed one is not retried in a loop.
    /// </summary>
    public static IQueryable<SongMetadata> WhereDueForAutoResync(this IQueryable<SongMetadata> query)
        => query
            .WhereEligibleForLyricsTimingProbe()
            .Where(s => s.LyricsSyncStatus == LyricsSyncStatus.Suspect)
            .Where(s => s.LyricsSyncProbeAttempts > 0)
            .Where(s => s.TranscriptionStatus == TranscriptionStatus.NotRequested)
            .Where(s => s.IsInstrumental != true)
            .Where(s => s.PlainLyrics != null && s.PlainLyrics != string.Empty);
}
