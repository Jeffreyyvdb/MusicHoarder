using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.BackgroundWork.Charts;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Settings;

namespace MusicHoarder.Api.BackgroundWork.Insights;

/// <summary>
/// The lyrics sweep: where every song's lyrics come from, how well their timing fits, and what each of its
/// four passes has left. Every queue is counted with the query the pass itself takes songs from
/// (<see cref="LyricsTimingQueries"/>), and every song is sorted by <see cref="LyricsOutcomes.Classify"/>, which
/// shares the player's display rules, so a chart cannot count a song the sweep would not pick or describe
/// lyrics the player does not show.
/// </summary>
public sealed class LyricsInsight(
    MusicHoarderDbContext db,
    IOptionsMonitor<LyricsTimingOptions> options,
    IRuntimeSettingsService runtimeSettings,
    ILyricsTranscriptionService transcriber,
    BackgroundWorkStatus status,
    TimeProvider time) : IBackgroundJobInsight
{
    private const int DailyBuckets = 30;
    private const int RateWindowDays = 7;

    public string Id => BackgroundJobIds.Lyrics;

    private enum Finished { AiTimed, AiWritten, NoSinging, Failed, Shifted }

    private sealed record AiQueueSong(int Id, int? DurationSeconds, int? DurationMs);

    private sealed record Data(
        DateTime Now,
        LyricsTimingOptions Options,
        bool AutoSyncOn,
        Dictionary<string, int> Outcomes,
        int SyncedSongs,
        List<AiQueueSong> AiQueue,
        double AiQueueSeconds,
        double AiRatePerDay,
        List<(DateTime At, Finished Kind)> Finished)
    {
        public int Population => Outcomes.Values.Sum();
        public int WithoutNeed => Outcomes.Where(o => LyricsOutcomes.NeedsNoWords(o.Key)).Sum(o => o.Value);
    }

    private IQueryable<SongMetadata> AiDueQueue(LyricsTimingOptions opts)
        => db.Songs.AsNoTracking().WhereDueForAutoSync().WhereFitsBackgroundAudioCeiling(opts.BackgroundAudioCeilingSeconds);

    private async Task<Data> LoadAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var opts = options.CurrentValue;

        var facts = await db.Songs.AsNoTracking()
            .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic && !s.IsDuplicate)
            .ExcludingDemoTenant()
            .Select(s => new LyricsFacts(
                s.LyricsStatus,
                s.LyricsOrigin,
                s.IsInstrumental,
                s.SyncedLyrics != null && s.SyncedLyrics != "",
                s.PlainLyrics != null && s.PlainLyrics != "",
                s.TranscribedSyncedLyrics != null && s.TranscribedSyncedLyrics != "",
                s.TranscribedPlainLyrics != null && s.TranscribedPlainLyrics != "",
                s.PreferredLyricsSource,
                s.TranscriptionStatus,
                s.TranscriptionAlignedToReference,
                s.LyricsSyncOffsetMs != null,
                !string.IsNullOrWhiteSpace(s.Title) && !string.IsNullOrWhiteSpace(s.Artist)))
            .ToListAsync(ct);

        var outcomes = LyricsOutcomes.Segments.ToDictionary(s => s.Key, _ => 0);
        var synced = 0;
        foreach (var fact in facts)
        {
            var outcome = LyricsOutcomes.Classify(fact);
            outcomes[outcome.Key]++;
            if (outcome.ShowsSyncedLyrics)
                synced++;
        }

        var queue = await AiDueQueue(opts)
            .Select(s => new AiQueueSong(s.Id, s.DurationSeconds, s.DurationMs))
            .ToListAsync(ct);
        var queueSeconds = queue.Sum(s => TranscriptionAudioBudget.WholeSongSeconds(s.DurationSeconds, s.DurationMs));

        var weekAgo = now.AddDays(-RateWindowDays);
        var aiSecondsThisWeek = await db.TranscriptionAudioSpends.AsNoTracking()
            .Where(e => e.Purpose == TranscriptionPurpose.AutoSync && e.AtUtc > weekAgo)
            .SumAsync(e => e.AudioSeconds, ct);
        var oldestSpend = await db.TranscriptionAudioSpends.AsNoTracking().MinAsync(e => (DateTime?)e.AtUtc, ct);

        var monthAgo = now.AddDays(-DailyBuckets);
        var transcribed = await db.Songs.AsNoTracking()
            .Where(s => s.TranscribedAtUtc > monthAgo)
            .Select(s => new
            {
                At = s.TranscribedAtUtc!.Value,
                s.TranscriptionStatus,
                HasSynced = s.TranscribedSyncedLyrics != null && s.TranscribedSyncedLyrics != "",
                s.TranscriptionAlignedToReference,
            })
            .ToListAsync(ct);
        var shifted = await db.Songs.AsNoTracking()
            .Where(s => s.LyricsSyncStatus == LyricsSyncStatus.Corrected && s.LyricsSyncCheckedAtUtc > monthAgo)
            .Select(s => s.LyricsSyncCheckedAtUtc!.Value)
            .ToListAsync(ct);

        var finished = transcribed
            .Select(t => (t.At, Kind: t.TranscriptionStatus == TranscriptionStatus.Failed ? Finished.Failed
                : !t.HasSynced ? Finished.NoSinging
                : t.TranscriptionAlignedToReference ? Finished.AiTimed
                : Finished.AiWritten))
            .Concat(shifted.Select(t => (t, Finished.Shifted)))
            .ToList();

        var autoSyncOn = transcriber.IsConfigured && (await runtimeSettings.GetAsync(ct)).LyricsAutoSyncEnabled;
        return new Data(
            now, opts, autoSyncOn, outcomes, synced, queue, queueSeconds,
            EtaEstimator.RatePerDay(aiSecondsThisWeek, oldestSpend, now, RateWindowDays), finished);
    }

    public async Task<BackgroundJobSummaryDto> SummarizeAsync(CancellationToken ct)
        => Summarize(await LoadAsync(ct));

    private BackgroundJobSummaryDto Summarize(Data d)
    {
        var needWords = d.Population - d.WithoutNeed;
        var eta = d.AutoSyncOn ? EtaEstimator.Eta(d.AiQueueSeconds, d.AiRatePerDay, d.Now) : null;
        string? etaLabel = null;
        if (d.AiQueue.Count > 0)
        {
            etaLabel = !d.AutoSyncOn
                ? $"AI sync is off; {BackgroundText.Count(d.AiQueue.Count)} songs ({BackgroundText.Duration(d.AiQueueSeconds)} of audio) would wait for it."
                : d.AiRatePerDay > 0
                    ? $"AI sync of {BackgroundText.Count(d.AiQueue.Count)} songs ({BackgroundText.Duration(d.AiQueueSeconds)} of audio) at the last {RateWindowDays} days' rate of {BackgroundText.Duration(d.AiRatePerDay)} a day."
                    : $"AI sync has sent no audio in the last {RateWindowDays} days, so there is no rate to estimate from.";
        }

        return BackgroundJobSummaries.Build(
            BackgroundJobDefinitions.Lyrics,
            null,
            status.Get(Id),
            d.Finished.Count == 0 ? null : d.Finished.Max(f => f.At),
            new BackgroundHeadlineDto(
                d.SyncedSongs, needWords, BackgroundFormat.Count,
                $"of {BackgroundText.Count(needWords)} songs with words have time-synced lyrics", eta, etaLabel),
            TimeBuckets.DailyCounts(
                d.Now, TimeBuckets.TrendDays,
                d.Finished.Where(f => f.Kind is Finished.AiTimed or Finished.AiWritten).Select(f => f.At)),
            "songs given AI-timed lyrics per day");
    }

    public async Task<BackgroundJobDetailDto> DescribeAsync(CancellationToken ct)
    {
        var d = await LoadAsync(ct);
        var opts = d.Options;

        var outcomeSegments = LyricsOutcomes.Segments
            .Select(s => new BackgroundPanels.Segment(s.Key, s.Label, d.Outcomes[s.Key], s.Tone, s.Note));

        var verdictGroups = await db.Songs.AsNoTracking()
            .WhereEligibleForLyricsTimingCheck()
            .GroupBy(s => new { s.LyricsSyncStatus, Probed = s.LyricsSyncProbeAttempts > 0 })
            .Select(g => new { g.Key.LyricsSyncStatus, g.Key.Probed, Count = g.Count() })
            .ToListAsync(ct);
        long Verdicts(LyricsSyncStatus status, bool? probed = null)
            => verdictGroups.Where(g => g.LyricsSyncStatus == status && (probed is null || g.Probed == probed)).Sum(g => g.Count);

        // The queues, each with the pass's own query.
        var freeCheck = await db.Songs.AsNoTracking().WhereDueForFreeTimingCheck().CountAsync(ct);
        var suspectIds = await db.Songs.AsNoTracking()
            .WhereDueForSuspectProbe(opts.MaxProbeAttempts).Select(s => s.Id).ToListAsync(ct);
        var unconfirmedIds = opts.ProbeUnconfirmedSongs
            ? await db.Songs.AsNoTracking().WhereDueForUnconfirmedProbe().Select(s => s.Id).ToListAsync(ct)
            : [];
        var givenUp = await db.Songs.AsNoTracking()
            .WhereEligibleForLyricsTimingProbe()
            .CountAsync(s => s.LyricsSyncStatus == LyricsSyncStatus.Suspect && s.LyricsSyncProbeAttempts >= opts.MaxProbeAttempts, ct);
        var resync = await db.Songs.AsNoTracking()
            .WhereDueForAutoResync()
            .WhereFitsBackgroundAudioCeiling(opts.BackgroundAudioCeilingSeconds)
            .Select(s => new AiQueueSong(s.Id, s.DurationSeconds, s.DurationMs))
            .ToListAsync(ct);
        var aiWholeQueue = await db.Songs.AsNoTracking().WhereDueForAutoSync().CountAsync(ct);

        double Audio(IEnumerable<AiQueueSong> songs)
            => songs.Sum(s => TranscriptionAudioBudget.WholeSongSeconds(s.DurationSeconds, s.DurationMs));
        var probeCount = suspectIds.Count + unconfirmedIds.Count;

        var aiMap = await AiSyncMapAsync(d, ct);
        var probeMap = await ProbeMapAsync(suspectIds, unconfirmedIds, ct);

        var panels = new List<BackgroundPanelDto>
        {
            BackgroundPanels.Breakdown(
                "Where every song's lyrics come from",
                "Each song is counted once, by the lyrics the player shows for it.", "songs", outcomeSegments),
            BackgroundPanels.Breakdown(
                "Timing verdicts", "Songs with an LRC, and whether its timing fits the audio.", "songs",
            [
                new("not-checked", "Not checked yet", Verdicts(LyricsSyncStatus.NotChecked), BackgroundTone.Pending),
                new("ok-free", "Fits, by the free checks", Verdicts(LyricsSyncStatus.Ok, probed: false), BackgroundTone.Good,
                    "Nothing provably wrong; no one has listened."),
                new("ok-probed", "Fits, confirmed by listening", Verdicts(LyricsSyncStatus.Ok, probed: true), BackgroundTone.Good),
                new("corrected", "Shifted into place by AI", Verdicts(LyricsSyncStatus.Corrected), BackgroundTone.Ai,
                    "One constant offset, measured and applied."),
                new("suspect", "Looks wrong", Verdicts(LyricsSyncStatus.Suspect, probed: false), BackgroundTone.Warning),
                new("suspect-probed", "Heard drifting", Verdicts(LyricsSyncStatus.Suspect, probed: true), BackgroundTone.Bad,
                    "No single shift fixes it; a re-sync can."),
                new("unverifiable", "Could not be judged", Verdicts(LyricsSyncStatus.Unverifiable), BackgroundTone.Muted),
            ]),
            new FiguresPanelDto("What each pass has left",
                "Each is counted with the query its pass takes songs from.",
            [
                BackgroundPanels.Count("Free timing check", freeCheck, "arithmetic only, no audio"),
                BackgroundPanels.Count(
                    "Timing probes", probeCount,
                    opts.EnableProbeSweep
                        ? $"{BackgroundText.Count(suspectIds.Count)} flagged first, then {BackgroundText.Count(unconfirmedIds.Count)} nobody has heard"
                        : "the probe sweep is switched off"),
                BackgroundPanels.Duration("Probe audio", probeCount * opts.ProbeWindowSeconds, $"{BackgroundText.Duration(opts.ProbeWindowSeconds)} listened to per song"),
                BackgroundPanels.Count("Probes given up", givenUp, $"heard {opts.MaxProbeAttempts} times and still suspect", givenUp > 0 ? BackgroundTone.Warning : BackgroundTone.Neutral),
                BackgroundPanels.Count("Re-sync queue", resync.Count, opts.EnableAutoResync ? "heard drifting, words to align to" : "automatic re-sync is switched off"),
                BackgroundPanels.Duration("Re-sync audio", Audio(resync)),
                BackgroundPanels.Count("AI sync queue", d.AiQueue.Count, d.AutoSyncOn ? "no LRC, lyrics looked up" : "AI sync is off, so these wait"),
                BackgroundPanels.Duration("AI sync audio", d.AiQueueSeconds),
                BackgroundPanels.Count(
                    "Too long for the audio ceiling", aiWholeQueue - d.AiQueue.Count,
                    $"songs over {BackgroundText.Duration(opts.BackgroundAudioCeilingSeconds)} are never picked"),
            ]),
            aiMap,
            probeMap,
            new SeriesPanelDto(
                "What the sweep finished each day, last 30 days",
                "A song counts on the day of its latest transcription or timing correction, and presses of the AI button count too.",
                "day", BackgroundFormat.Count, true,
                [
                    new("ai-timed", "Official words, timed by AI", BackgroundTone.Ai),
                    new("ai-written", "Words written by AI", BackgroundTone.Ai),
                    new("no-singing", "No singing heard", BackgroundTone.Muted),
                    new("failed", "Transcription failed", BackgroundTone.Bad),
                    new("shifted", "Timing shifted into place", BackgroundTone.Pending),
                ],
                TimeBuckets.Fill(d.Now, hourly: false, DailyBuckets, 5, d.Finished.Select(f => (f.At, (int)f.Kind, 1.0))),
                []),
        };

        return new BackgroundJobDetailDto(Summarize(d), panels);
    }

    /// <summary>
    /// The whole library in the order the AI sync pass works through it. A song is waiting when the pass's own
    /// query would pick it, done when it has been transcribed and is no longer due, and otherwise not this
    /// pass's business (it has an LRC, is a duplicate, has not had its lyrics looked up, is too long).
    /// </summary>
    private async Task<LibraryMapPanelDto> AiSyncMapAsync(Data d, CancellationToken ct)
    {
        var waiting = d.AiQueue.Select(s => s.Id).ToHashSet();
        var songs = await db.Songs.AsNoTracking()
            .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic)
            .ExcludingDemoTenant()
            .OrderBy(LyricsTimingQueries.AddedAt).ThenBy(s => s.Id)
            .Select(s => new
            {
                s.Id,
                Added = s.AcquiredAtUtc ?? s.IndexedAtUtc,
                HasLrc = s.SyncedLyrics != null && s.SyncedLyrics != "",
                s.TranscriptionStatus,
            })
            .ToListAsync(ct);

        var items = songs.Select(s => new MapItem(
            s.Added,
            waiting.Contains(s.Id) ? MapState.Waiting
            : !s.HasLrc && s.TranscriptionStatus is TranscriptionStatus.Completed or TranscriptionStatus.Failed ? MapState.Done
            : MapState.Excluded)).ToList();

        var markers = new List<LibraryMapMarkerDto>();
        if (LibraryMapBinner.WaitingFrontier(items) is { } frontier)
        {
            markers.Add(new("Oldest end has reached here", frontier.Oldest));
            if (frontier.Newest != frontier.Oldest)
                markers.Add(new("Newest end has reached here", frontier.Newest));
        }

        return new LibraryMapPanelDto(
            "Where AI sync has reached",
            "The whole library, oldest addition on the left. The pass takes songs from both ends in turn, so the done stretches close in on the middle.",
            "Transcribed", "Waiting for AI sync", "Not AI sync's to do",
            LibraryMapBinner.Bin(items), markers);
    }

    /// <summary>
    /// The songs with an LRC in id order, which is the order the probe pass takes them in, read backwards:
    /// newest first. Waiting means the pass's own queries pick the song; done means a probe has heard it.
    /// </summary>
    private async Task<LibraryMapPanelDto> ProbeMapAsync(
        List<int> suspectIds, List<int> unconfirmedIds, CancellationToken ct)
    {
        var waiting = suspectIds.Concat(unconfirmedIds).ToHashSet();
        var songs = await db.Songs.AsNoTracking()
            .WhereEligibleForLyricsTimingCheck()
            .OrderBy(s => s.Id)
            .Select(s => new { s.Id, Added = s.AcquiredAtUtc ?? s.IndexedAtUtc, s.LyricsSyncProbeAttempts })
            .ToListAsync(ct);

        var items = songs.Select(s => new MapItem(
            s.Added,
            waiting.Contains(s.Id) ? MapState.Waiting
            : s.LyricsSyncProbeAttempts > 0 ? MapState.Done
            : MapState.Excluded)).ToList();

        var markers = new List<LibraryMapMarkerDto>();
        if (LibraryMapBinner.WaitingFrontier(items) is { } frontier)
            markers.Add(new("Probes have reached here, newest first", frontier.Newest));

        return new LibraryMapPanelDto(
            "Where the timing probes have reached",
            "Songs with an LRC, oldest addition on the left. The probe pass starts at the newest and works back, flagged songs first.",
            "Heard", "Waiting for a probe", "Not probed",
            LibraryMapBinner.Bin(items), markers);
    }
}
