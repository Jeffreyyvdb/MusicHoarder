using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.BackgroundWork.Charts;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.BackgroundWork.Insights;

public sealed class TranscriptionAllowanceInsight(
    MusicHoarderDbContext db,
    IOptionsMonitor<LyricsTimingOptions> options,
    IOptionsMonitor<LyricsTranscriptionOptions> transcription,
    ILyricsTranscriptionService transcriber,
    TimeProvider time) : IBackgroundJobInsight
{
    private const int HourlyBuckets = 48;
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

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
        bool OnGroq,
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
        var since = now.AddDays(-TranscriptionAudioBudget.HistoryDays);
        var spend = await db.TranscriptionAudioSpends.AsNoTracking()
            .Where(e => e.AtUtc > since)
            .OrderBy(e => e.AtUtc)
            .ToListAsync(ct);

        var lastDay = spend.Where(e => e.AtUtc > now - Day).ToList();
        var smallestRequest = opts.ProbeWindowSeconds;
        return new Ledger(
            now, opts, TranscriptionAudioBudget.IsGroq(transcription.CurrentValue), spend,
            HourSpent: spend.Where(e => e.AtUtc > now - Hour).Sum(e => e.AudioSeconds),
            DaySpent: lastDay.Sum(e => e.AudioSeconds),
            HourWait: TranscriptionAudioBudget.WaitForRoom(lastDay, now, Hour, opts.AudioSecondsPerHour, smallestRequest),
            DayWait: TranscriptionAudioBudget.WaitForRoom(lastDay, now, Day, opts.AudioSecondsPerDay, smallestRequest));
    }

    public async Task<BackgroundJobSummaryDto> SummarizeAsync(CancellationToken ct)
        => Summarize(await LoadAsync(ct));

    private BackgroundJobSummaryDto Summarize(Ledger l)
    {
        var background = l.Spend.Where(e => TranscriptionAudioBudget.IsBackground(e.Purpose)).ToList();
        var lastBackground = background.Count == 0 ? (DateTime?)null : background.Max(e => e.AtUtc);

        JobRuntime runtime;
        if (l.Wait > TimeSpan.Zero)
        {
            runtime = new JobRuntime(
                BackgroundJobState.Waiting,
                l.HourWait >= l.DayWait
                    ? "Background transcription is waiting for the hourly audio allowance."
                    : "Background transcription is waiting for the daily audio allowance.",
                l.Now + l.Wait, l.Now + l.Wait, l.Now, lastBackground);
        }
        else
        {
            var busy = lastBackground is { } last && l.Now - last < TimeSpan.FromMinutes(10);
            runtime = new JobRuntime(
                busy ? BackgroundJobState.Working : BackgroundJobState.Idle, null, null, null, l.Now, lastBackground);
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

        var hourLimits = new List<SeriesLimitDto>
        {
            new("Background ceiling per hour", o.AudioSecondsPerHour, BackgroundTone.Warning),
        };
        var dayLimits = new List<SeriesLimitDto>
        {
            new("Background ceiling per day", o.AudioSecondsPerDay, BackgroundTone.Warning),
        };
        if (l.OnGroq)
        {
            hourLimits.Add(new("Groq free tier per hour", TranscriptionAudioBudget.GroqFreeTierAudioSecondsPerHour, BackgroundTone.Bad));
            dayLimits.Add(new("Groq free tier per day", TranscriptionAudioBudget.GroqFreeTierAudioSecondsPerDay, BackgroundTone.Bad));
        }
        dayLimits.Add(new("Share the timing probes may use", o.ProbeAudioSecondsPerDay, BackgroundTone.Pending));

        FigureDto[] keptForManualRuns = l.OnGroq
            ? [BackgroundPanels.Duration(
                "Kept for manual runs",
                Math.Max(0, TranscriptionAudioBudget.GroqFreeTierAudioSecondsPerDay - l.DaySpent),
                $"left of Groq's {BackgroundText.Duration(TranscriptionAudioBudget.GroqFreeTierAudioSecondsPerDay)} a day on the free tier",
                BackgroundTone.Good)]
            : [];
        var panels = new List<BackgroundPanelDto>
        {
            new SeriesPanelDto(
                "Audio sent per hour, last 48 hours",
                "Clock hours, UTC. The ceilings apply to any rolling hour, so a bar can sit just above a line without the budget being broken.",
                "hour", BackgroundFormat.Duration, true, seriesDefs,
                TimeBuckets.Fill(l.Now, hourly: true, HourlyBuckets, Series.Length, samples),
                hourLimits),
            new SeriesPanelDto(
                $"Audio sent per day, last {TranscriptionAudioBudget.HistoryDays} days",
                "UTC days. History starts when the API first kept it, so older days can look emptier than they were.",
                "day", BackgroundFormat.Duration, true, seriesDefs,
                TimeBuckets.Fill(l.Now, hourly: false, TranscriptionAudioBudget.HistoryDays, Series.Length, samples),
                dayLimits),
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
                ..keptForManualRuns,
                BackgroundPanels.Count("Requests in the last 24 hours", requests),
                BackgroundPanels.Duration("Average request", requests == 0 ? 0 : l.DaySpent / requests, "of audio each"),
            ]),
        };

        return new BackgroundJobDetailDto(Summarize(l), panels);
    }
}
