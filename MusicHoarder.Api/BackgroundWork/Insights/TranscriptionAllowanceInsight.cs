using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.BackgroundWork.Charts;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.BackgroundWork.Insights;

/// <summary>
/// The transcription provider's audio-seconds meter, from <see cref="TranscriptionAudioSpend"/>, the rows
/// <see cref="TranscriptionAudioBudget"/> keeps. The window arithmetic is the budget's own
/// (<c>WaitForRoom</c>), so "refused until" is when the sweeps' next request would actually be let through.
/// </summary>
public sealed class TranscriptionAllowanceInsight(
    MusicHoarderDbContext db,
    IOptionsMonitor<LyricsTimingOptions> options,
    ILyricsTranscriptionService transcriber,
    TimeProvider time) : IBackgroundJobInsight
{
    private const int HourlyBuckets = 48;
    private const int DailyBuckets = 30;
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    /// <summary>The ledger's purposes as the page names them. Hand-made requests are one series.</summary>
    private static readonly (string Key, string Label, string Tone)[] Series =
    [
        ("ai-sync", "AI sync", BackgroundTone.Ai),
        ("resync", "Re-sync", BackgroundTone.Pending),
        ("probe", "Timing probes", BackgroundTone.Neutral),
        ("manual", "By hand", BackgroundTone.Muted),
    ];

    public string Id => BackgroundJobIds.TranscriptionAllowance;

    private static int SeriesOf(TranscriptionPurpose purpose) => purpose switch
    {
        TranscriptionPurpose.AutoSync => 0,
        TranscriptionPurpose.AutoResync => 1,
        TranscriptionPurpose.TimingProbe => 2,
        _ => 3,
    };

    private sealed record Ledger(
        DateTime Now,
        LyricsTimingOptions Options,
        List<TranscriptionAudioSpend> Spend,
        double HourSpent,
        double DaySpent,
        TimeSpan HourWait,
        TimeSpan DayWait)
    {
        public double HourLeft => Math.Max(0, Options.AudioSecondsPerHour - HourSpent);
        public double DayLeft => Math.Max(0, Options.AudioSecondsPerDay - DaySpent);
        public TimeSpan Wait => HourWait > DayWait ? HourWait : DayWait;
    }

    private async Task<Ledger> LoadAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var opts = options.CurrentValue;
        var since = now.AddDays(-DailyBuckets);
        var spend = await db.TranscriptionAudioSpends.AsNoTracking()
            .Where(e => e.AtUtc > since)
            .OrderBy(e => e.AtUtc)
            .ToListAsync(ct);

        var lastDay = spend.Where(e => e.AtUtc > now - Day).ToList();
        // What a probe window, the smallest request a sweep makes, would be told right now.
        var probe = opts.ProbeWindowSeconds;
        return new Ledger(
            now, opts, spend,
            HourSpent: spend.Where(e => e.AtUtc > now - Hour).Sum(e => e.AudioSeconds),
            DaySpent: lastDay.Sum(e => e.AudioSeconds),
            HourWait: TranscriptionAudioBudget.WaitForRoom(lastDay, now, Hour, opts.AudioSecondsPerHour, probe),
            DayWait: TranscriptionAudioBudget.WaitForRoom(lastDay, now, Day, opts.AudioSecondsPerDay, probe));
    }

    public async Task<BackgroundJobSummaryDto> SummarizeAsync(CancellationToken ct)
        => Summarize(await LoadAsync(ct));

    private BackgroundJobSummaryDto Summarize(Ledger l)
    {
        var background = l.Spend.Where(e => TranscriptionAudioBudget.IsBackground(e.Purpose)).ToList();
        var lastBackground = background.Count == 0 ? (DateTime?)null : background.Max(e => e.AtUtc);

        JobRuntime? runtime = null;
        if (l.Wait > TimeSpan.Zero)
        {
            var hourly = l.HourWait >= l.DayWait;
            runtime = new JobRuntime(
                BackgroundJobState.Waiting,
                hourly
                    ? "Background transcription is waiting for the hourly audio allowance."
                    : "Background transcription is waiting for the daily audio allowance.",
                l.Now + l.Wait, l.Now + l.Wait, l.Now, lastBackground);
        }
        else if (lastBackground is { } last && l.Now - last < TimeSpan.FromMinutes(10))
        {
            runtime = new JobRuntime(BackgroundJobState.Working, null, null, null, l.Now, lastBackground);
        }
        else
        {
            runtime = new JobRuntime(BackgroundJobState.Idle, null, null, null, l.Now, lastBackground);
        }

        var perDay = (double)l.Options.AudioSecondsPerDay;
        return BackgroundJobSummaries.Build(
            BackgroundJobDefinitions.TranscriptionAllowance,
            transcriber.IsConfigured ? null : "No transcription provider is configured, so nothing is sent for transcription.",
            runtime,
            lastBackground,
            new BackgroundHeadlineDto(
                l.DayLeft, perDay, BackgroundFormat.Duration,
                "of the day's background audio allowance is left (rolling 24 hours)", null, null),
            TimeBuckets.DailyTotals(l.Now, TimeBuckets.TrendDays, l.Spend.Select(e => (e.AtUtc, e.AudioSeconds))),
            "audio seconds sent per day");
    }

    public async Task<BackgroundJobDetailDto> DescribeAsync(CancellationToken ct)
    {
        var l = await LoadAsync(ct);
        var o = l.Options;

        var seriesDefs = Series.Select(s => new SeriesDefinitionDto(s.Key, s.Label, s.Tone)).ToList();
        var samples = l.Spend.Select(e => (e.AtUtc, SeriesOf(e.Purpose), e.AudioSeconds)).ToList();

        var lastDay = l.Spend.Where(e => e.AtUtc > l.Now - Day).ToList();
        var perPurpose = Series.Select((s, i) => new BackgroundPanels.Segment(
            s.Key, s.Label, (long)Math.Round(lastDay.Where(e => SeriesOf(e.Purpose) == i).Sum(e => e.AudioSeconds)), s.Tone));

        var requests = lastDay.Count;
        var panels = new List<BackgroundPanelDto>
        {
            new SeriesPanelDto(
                "Audio sent per hour, last 48 hours",
                "Clock hours, UTC. The ceilings apply to any rolling hour, so a bar can sit just above a line without the budget being broken.",
                "hour", BackgroundFormat.Duration, true, seriesDefs,
                TimeBuckets.Fill(l.Now, hourly: true, HourlyBuckets, Series.Length, samples),
                [
                    new SeriesLimitDto("Background ceiling per hour", o.AudioSecondsPerHour, BackgroundTone.Warning),
                    new SeriesLimitDto("Provider limit per hour", TranscriptionAudioBudget.ProviderAudioSecondsPerHour, BackgroundTone.Bad),
                ]),
            new SeriesPanelDto(
                "Audio sent per day, last 30 days",
                "UTC days. History starts when the API first kept it, so older days can look emptier than they were.",
                "day", BackgroundFormat.Duration, true, seriesDefs,
                TimeBuckets.Fill(l.Now, hourly: false, DailyBuckets, Series.Length, samples),
                [
                    new SeriesLimitDto("Background ceiling per day", o.AudioSecondsPerDay, BackgroundTone.Warning),
                    new SeriesLimitDto("Provider limit per day", TranscriptionAudioBudget.ProviderAudioSecondsPerDay, BackgroundTone.Bad),
                    new SeriesLimitDto("Share the timing probes may use", o.ProbeAudioSecondsPerDay, BackgroundTone.Pending),
                ]),
            BackgroundPanels.Breakdown(
                "Where the last 24 hours went", "Audio seconds, by what asked for them.", "seconds", perPurpose),
            new FiguresPanelDto("The allowance right now", "Background spending stops at the ceilings, which sit below the provider's own limits so a manual run always has room.",
            [
                BackgroundPanels.Duration("Spent in the last hour", l.HourSpent,
                    $"of a {BackgroundText.Duration(o.AudioSecondsPerHour)} background ceiling",
                    l.HourLeft <= 0 ? BackgroundTone.Warning : BackgroundTone.Neutral),
                BackgroundPanels.Duration("Left this hour", l.HourLeft, "for background work", l.HourLeft <= 0 ? BackgroundTone.Warning : BackgroundTone.Good),
                BackgroundPanels.Duration("Spent in the last 24 hours", l.DaySpent,
                    $"of a {BackgroundText.Duration(o.AudioSecondsPerDay)} background ceiling",
                    l.DayLeft <= 0 ? BackgroundTone.Warning : BackgroundTone.Neutral),
                BackgroundPanels.Duration("Left today", l.DayLeft, "for background work", l.DayLeft <= 0 ? BackgroundTone.Warning : BackgroundTone.Good),
                BackgroundPanels.Duration(
                    "Kept for manual runs",
                    Math.Max(0, TranscriptionAudioBudget.ProviderAudioSecondsPerDay - l.DaySpent),
                    $"left of the provider's {BackgroundText.Duration(TranscriptionAudioBudget.ProviderAudioSecondsPerDay)} a day",
                    BackgroundTone.Good),
                BackgroundPanels.Count("Requests in the last 24 hours", requests),
                BackgroundPanels.Duration("Average request", requests == 0 ? 0 : l.DaySpent / requests, "of audio each"),
            ]),
        };

        return new BackgroundJobDetailDto(Summarize(l), panels);
    }
}
