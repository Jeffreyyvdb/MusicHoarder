using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment;

/// <summary>
/// Background sweep that keeps every song's lyric timing verdict current, fixes what it can, and gives songs
/// with no LRC AI-timed lyrics, in four passes with very different costs.
///
/// The <b>free pass</b> runs the arithmetic checks over any song that has never been judged. It touches no
/// API, so it is allowed to chew through the entire library as fast as the database will serve it — which is
/// how a pre-existing collection gets verdicts without a migration-time backfill.
///
/// The <b>probe pass</b> spends real transcription quota, so it is bounded per batch and every window goes
/// through <see cref="TranscriptionAudioBudget"/>, which keeps the sweeps out of the share left for manual runs. Songs the free pass flagged as Suspect go first; after
/// them come songs the free checks passed but no probe has heard, because those checks only catch gross
/// mistakes and a sloppily timed LRC sails through them. A song that has burned its probe attempts is left
/// alone: the point of the budget is that an unfixable track cannot quietly consume the allowance the fixable
/// ones need.
///
/// The <b>re-sync pass</b> acts on what the probe found. A probe that measures one constant offset repairs the
/// LRC on the spot; one that finds the timing drifting leaves the song Suspect, and this pass then runs the
/// same full transcription and forced alignment as the "Sync with AI" button. It costs a whole song's audio,
/// so it is capped per day and draws on the same budget.
///
/// The <b>AI sync pass</b> transcribes songs that have no LRC at all, taking the newest and the oldest
/// additions in turn. It gets whatever background audio the probes, capped per day, leave over.
/// </summary>
public sealed partial class LyricsTimingCheckService(
    IServiceScopeFactory scopeFactory,
    LyricsTimingProbe probe,
    ILyricsTranscriptionService transcriber,
    TranscriptionAudioBudget budget,
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

    /// <summary>The same, for the AI sync pass.</summary>
    private DateTime _autoSyncDeferredUntilUtc = DateTime.MinValue;

    private const int ResyncSongsPerPass = 3;

    /// <summary>Half from the newest end of the library, half from the oldest.</summary>
    private const int AutoSyncSongsPerPass = 4;

    /// <summary>
    /// Fewer letters than this in a transcription of a song with no official lyrics means nobody sang: Whisper
    /// fills an instrumental with a stock phrase or two ("Thank you for watching" is 20), which must not be
    /// shown as lyrics. A dozen sung English words is about fifty.
    /// </summary>
    private const int MinHeardLetters = 40;

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
                // A song with no LRC at all goes before checking an LRC that is most likely fine.
                worked |= await RunAutoSyncPassAsync(stoppingToken);
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

        var shareLeft = opts.ProbeAudioSecondsPerDay
            - await budget.SecondsLastDayAsync(TranscriptionPurpose.TimingProbe, ct);
        var batchSize = Math.Min(opts.SweepBatchSize, (int)(shareLeft / opts.ProbeWindowSeconds));
        if (batchSize <= 0)
            return false;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var songs = await TakeAudibleAsync(
            db,
            db.Songs
                .IgnoreQueryFilters()
                .WhereEligibleForLyricsTimingProbe()
                .Where(s => s.LyricsSyncStatus == LyricsSyncStatus.Suspect)
                .Where(s => s.LyricsSyncProbeAttempts < opts.MaxProbeAttempts)
                .OrderBy(s => s.LyricsSyncProbeAttempts)
                .ThenByDescending(s => s.Id),
            batchSize,
            ct);

        if (songs.Count < batchSize && opts.ProbeUnconfirmedSongs)
        {
            // "Ok" with no probe attempt means only the arithmetic has looked at it: nothing provably wrong,
            // which a uniformly shifted or carelessly timed LRC also satisfies. Newest first, so what was
            // just added is checked before the back catalogue.
            songs.AddRange(await TakeAudibleAsync(
                db,
                db.Songs
                    .IgnoreQueryFilters()
                    .WhereEligibleForLyricsTimingProbe()
                    .Where(s => s.LyricsSyncStatus == LyricsSyncStatus.Ok)
                    .Where(s => s.LyricsSyncProbeAttempts == 0)
                    .OrderByDescending(s => s.Id),
                batchSize - songs.Count,
                ct));
        }

        if (songs.Count == 0)
            return false;

        var probed = 0;
        foreach (var (song, path) in songs)
        {
            ct.ThrowIfCancellationRequested();

            LyricsProbeResult? result;
            try
            {
                result = await probe.ProbeAsync(song, path, TranscriptionPurpose.TimingProbe, ct);
            }
            catch (TranscriptionRateLimitedException ex)
            {
                // Out of budget, or the provider is rate-limiting us. Either way nothing was learned about
                // this song, so stop the batch and stand down until the limit has moved.
                _probeDeferredUntilUtc = DeferUntil(ex);
                logger.LogInformation(
                    "Lyrics timing probe deferred until {Until:HH:mm} UTC: {Reason}", _probeDeferredUntilUtc, ex.Message);
                break;
            }
            if (result is null)
                break;

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

        var remaining = opts.AutoResyncPerDay - await budget.CountLastDayAsync(TranscriptionPurpose.AutoResync, ct);
        if (remaining <= 0)
            return false;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var songs = await TakeAudibleAsync(
            db,
            db.Songs
                .IgnoreQueryFilters()
                .WhereDueForAutoResync()
                .WhereFitsBackgroundAudioCeiling(opts.BackgroundAudioCeilingSeconds)
                .OrderByDescending(s => s.Id),
            Math.Min(remaining, ResyncSongsPerPass),
            ct);

        if (songs.Count == 0)
            return false;

        var resynced = 0;
        foreach (var (song, path) in songs)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var result = await budget.SpendAsync(
                    TranscriptionPurpose.AutoResync,
                    TranscriptionAudioBudget.WholeSongSeconds(song),
                    () => transcriber.TranscribeAsync(song, path, ct),
                    ct);
                var applied = LyricsResync.Apply(song, result);
                logger.LogInformation(
                    "Lyrics auto re-sync for {Track} (SongId={SongId}): {Outcome}.",
                    song.TrackLabel,
                    song.Id,
                    applied.Promoted ? "official lyrics re-timed and now shown" : "transcribed, but the lyrics could not be aligned, so the LRCLIB version stays");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (TranscriptionRateLimitedException ex)
            {
                // We never got an answer, so nothing about the song is recorded and it stays due.
                _resyncDeferredUntilUtc = DeferUntil(ex);
                logger.LogInformation(
                    "Lyrics auto re-sync deferred until {Until:HH:mm} UTC: {Reason}", _resyncDeferredUntilUtc, ex.Message);
                break;
            }
            catch (Exception ex)
            {
                // Recorded as failed, which takes it out of the queue: a song that cannot be transcribed would
                // otherwise be retried on every pass. The button can still try it again.
                logger.LogWarning(ex, "Lyrics auto re-sync failed for SongId={SongId}.", song.Id);
                song.MarkTranscriptionFailed(ex.Message);
            }

            resynced++;
        }

        if (resynced > 0)
            await db.SaveChangesAsync(ct);

        return resynced > 0;
    }

    /// <summary>
    /// Transcribes songs that have no LRC, newest and oldest additions in turn, until the background audio
    /// ceiling stops it. A song with official words gets them re-timed and shown; one without gets the AI's
    /// words, which the player shows only while there is nothing official, or an instrumental mark when the
    /// AI heard no singing.
    /// </summary>
    internal async Task<bool> RunAutoSyncPassAsync(CancellationToken ct)
    {
        var opts = options.CurrentValue;
        if (!opts.EnableAutoSync || !transcriber.IsConfigured)
            return false;

        if (DateTime.UtcNow < _autoSyncDeferredUntilUtc)
            return false;

        List<(int Id, string Path)> picked;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
            var due = db.Songs
                .IgnoreQueryFilters()
                .WhereDueForAutoSync()
                .WhereFitsBackgroundAudioCeiling(opts.BackgroundAudioCeilingSeconds);
            var newest = await TakeAudibleAsync(
                db,
                due.OrderByDescending(s => s.AcquiredAtUtc ?? s.IndexedAtUtc).ThenByDescending(s => s.Id),
                AutoSyncSongsPerPass / 2,
                ct);
            var oldest = await TakeAudibleAsync(
                db,
                due.OrderBy(s => s.AcquiredAtUtc ?? s.IndexedAtUtc).ThenBy(s => s.Id),
                AutoSyncSongsPerPass / 2,
                ct);
            picked = newest.Zip(oldest, (n, o) => new[] { n, o })
                .SelectMany(pair => pair)
                .Concat(newest.Skip(oldest.Count))
                .Concat(oldest.Skip(newest.Count))
                .Select(p => (p.Song.Id, p.Path))
                .Distinct()
                .ToList();
        }

        if (picked.Count == 0)
            return false;

        var synced = 0;
        foreach (var (id, path) in picked)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await AutoSyncSongAsync(id, path, ct))
                    synced++;
            }
            catch (TranscriptionRateLimitedException ex)
            {
                _autoSyncDeferredUntilUtc = DeferUntil(ex);
                logger.LogInformation(
                    "Lyrics AI sync deferred until {Until:HH:mm} UTC: {Reason}", _autoSyncDeferredUntilUtc, ex.Message);
                break;
            }
        }

        return synced > 0;
    }

    /// <summary>
    /// Transcribes one song and saves the outcome on its own, so a restart mid-pass loses at most the song in
    /// flight. A transcription takes up to a minute, in which a lyrics re-check or the AI button may have
    /// changed the song, so it is read again before anything is written, and the result is dropped unless
    /// the song is still due.
    /// </summary>
    private async Task<bool> AutoSyncSongAsync(int id, string path, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var due = db.Songs.IgnoreQueryFilters().WhereDueForAutoSync();
        var song = await due.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (song is null)
            return false;

        TranscriptionResult? result = null;
        string? failure = null;
        try
        {
            result = await budget.SpendAsync(
                TranscriptionPurpose.AutoSync,
                TranscriptionAudioBudget.WholeSongSeconds(song),
                () => transcriber.TranscribeAsync(song, path, ct),
                ct);
        }
        catch (Exception ex) when (ex is not TranscriptionRateLimitedException
            && !(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Lyrics AI sync failed for SongId={SongId}.", id);
            failure = ex.Message;
        }

        await db.Entry(song).ReloadAsync(ct);
        if (!await due.AnyAsync(s => s.Id == id, ct))
        {
            logger.LogInformation(
                "Lyrics AI sync for SongId={SongId} dropped: the song changed while it was transcribed.", id);
            return true;
        }

        if (result is null)
            song.MarkTranscriptionFailed(failure ?? "The transcription returned nothing.");
        else
            ApplyAutoSync(song, result);

        await db.SaveChangesAsync(ct);
        return true;
    }

    private void ApplyAutoSync(SongMetadata song, TranscriptionResult result)
    {
        if (!result.AlignedToReference
            && string.IsNullOrWhiteSpace(song.PlainLyrics)
            && HeardLetters(result) < MinHeardLetters)
        {
            song.ApplyHeardNoSinging(result.Model);
            logger.LogInformation(
                "Lyrics AI sync for {Track} (SongId={SongId}): no singing heard, marked instrumental.",
                song.TrackLabel,
                song.Id);
            return;
        }

        // The transcriber looks the words up on LRCLIB itself when the song holds none. Words it found and
        // aligned to are the song's official lyrics now; without this the next lyrics re-check would bring
        // the same words as new and the song would be transcribed a second time to align them again.
        if (result.AlignedToReference && string.IsNullOrWhiteSpace(song.PlainLyrics))
            song.TryApplyLyricsUpgrade(null, result.PlainLyrics, instrumental: false);

        LyricsResync.Apply(song, result);
        logger.LogInformation(
            "Lyrics AI sync for {Track} (SongId={SongId}): {Outcome}.",
            song.TrackLabel,
            song.Id,
            result.AlignedToReference ? "official lyrics timed and now shown" : "transcribed from the audio");
    }

    /// <summary>
    /// Letters, not words, because Japanese, Chinese and Thai transcripts have no spaces between words. Read
    /// from both texts, since a transcript can come back without timestamps.
    /// </summary>
    private static int HeardLetters(TranscriptionResult result)
        => Math.Max(
            CountLetters(result.SyncedLyrics is null ? null : LrcTimestamp().Replace(result.SyncedLyrics, " ")),
            CountLetters(result.PlainLyrics));

    private static int CountLetters(string? text) => text?.Count(char.IsLetter) ?? 0;

    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex LrcTimestamp();

    private static async Task<List<(SongMetadata Song, string Path)>> TakeAudibleAsync(
        MusicHoarderDbContext db, IQueryable<SongMetadata> candidates, int count, CancellationToken ct)
    {
        if (count <= 0)
            return [];

        var picked = new List<(int Id, string Path)>(count);
        await foreach (var candidate in candidates
            .Select(s => new { s.Id, s.DestinationPath, s.SourcePath })
            .AsAsyncEnumerable()
            .WithCancellation(ct))
        {
            if (SongsEndpoints.ResolveAudioFilePath(candidate.DestinationPath, candidate.SourcePath) is not { } path)
                continue;
            picked.Add((candidate.Id, path));
            if (picked.Count == count)
                break;
        }

        if (picked.Count == 0)
            return [];

        var ids = picked.Select(p => p.Id).ToList();
        var songs = await db.Songs
            .IgnoreQueryFilters()
            .Where(s => ids.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, ct);
        return [.. picked.Select(p => (songs[p.Id], p.Path))];
    }

    /// <summary>
    /// When a pass that was told "not now" may try again: when it was told, but between a minute and an hour,
    /// so a provider answer of "tomorrow" costs one cheap refusal an hour rather than a stalled sweep.
    /// </summary>
    private static DateTime DeferUntil(TranscriptionRateLimitedException ex)
    {
        var wait = ex.RetryAfter ?? TimeSpan.Zero;
        if (wait < TimeSpan.FromMinutes(1))
            wait = TimeSpan.FromMinutes(1);
        if (wait > TimeSpan.FromHours(1))
            wait = TimeSpan.FromHours(1);
        return DateTime.UtcNow + wait;
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

    /// <summary>
    /// Songs the AI sync pass should transcribe: live, not a duplicate, not instrumental, no LRC, and lyrics
    /// already looked up (or impossible to look up, having no title or artist). Each is transcribed once, and again only when
    /// official words arrive or change after its transcription, which a lyrics re-check brings: that is what
    /// turns an AI guess into the official words re-timed. A failed transcription waits for the same.
    /// </summary>
    public static IQueryable<SongMetadata> WhereDueForAutoSync(this IQueryable<SongMetadata> query)
        => query
            .Where(s => s.DeletedAtUtc == null)
            .Where(s => !s.IsSynthetic)
            .ExcludingDemoTenant()
            .Where(s => !s.IsDuplicate)
            .Where(s => s.EnrichmentStatus == EnrichmentStatus.Matched || s.EnrichmentStatus == EnrichmentStatus.NeedsReview)
            .Where(s => s.SyncedLyrics == null || s.SyncedLyrics == string.Empty)
            .Where(s => s.IsInstrumental != true)
            .Where(s => s.LyricsStatus == LyricsStatus.Fetched
                || s.LyricsStatus == LyricsStatus.NotFound
                || s.LyricsStatus == LyricsStatus.Failed
                || (s.LyricsStatus == LyricsStatus.NotFetched
                    && (string.IsNullOrWhiteSpace(s.Title) || string.IsNullOrWhiteSpace(s.Artist))))
            .Where(s => s.TranscriptionStatus == TranscriptionStatus.NotRequested
                || (s.PlainLyrics != null && s.PlainLyrics != string.Empty
                    && s.LyricsTextChangedAtUtc != null
                    && (s.TranscribedAtUtc == null || s.LyricsTextChangedAtUtc > s.TranscribedAtUtc)));

    public static IQueryable<SongMetadata> WhereFitsBackgroundAudioCeiling(
        this IQueryable<SongMetadata> query, int ceilingSeconds)
        => query.Where(s => (s.DurationSeconds > 0 ? s.DurationSeconds.Value : TranscriptionAudioBudget.UnknownDurationSeconds)
            <= ceilingSeconds);
}
