using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.BackgroundWork.Charts;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.BackgroundWork.Insights;

public sealed class EnrichmentRetriesInsight(
    MusicHoarderDbContext db,
    IEnrichmentOrchestrator orchestrator,
    BackgroundWorkStatus status,
    TimeProvider time) : IBackgroundJobInsight
{
    private const int HourlyBuckets = 48;
    private static readonly TimeSpan StuckAfter = TimeSpan.FromDays(1);

    private static readonly (ProviderAttemptStatus Status, string Label, string Tone)[] Outcomes =
    [
        (ProviderAttemptStatus.Matched, "Matched", BackgroundTone.Good),
        (ProviderAttemptStatus.NoMatch, "Nothing found", BackgroundTone.Neutral),
        (ProviderAttemptStatus.RateLimited, "Rate limited", BackgroundTone.Warning),
        (ProviderAttemptStatus.Failed, "Failed", BackgroundTone.Bad),
        (ProviderAttemptStatus.Pending, "Pending", BackgroundTone.Pending),
    ];

    public string Id => BackgroundJobIds.EnrichmentRetries;

    private sealed record Data(
        DateTime Now,
        IReadOnlyCollection<EnrichmentProvider> Enabled,
        int DueSongs,
        int DueAttempts,
        List<(DateTime Day, int Count, DateTime Last)> AttemptsPerDay);

    private async Task<Data> LoadAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var enabled = await orchestrator.GetEnabledProviderEnumsAsync(ct);
        var due = db.SongProviderAttempts.AsNoTracking().WhereRetryableProviderAttempts(now, enabled);
        var dueSongs = await due.Select(a => a.SongId).Distinct().CountAsync(ct);
        var dueAttempts = await due.CountAsync(ct);

        var since = TimeBuckets.Starts(now, hourly: false, TimeBuckets.TrendDays)[0];
        var perDay = (await db.SongProviderAttempts.AsNoTracking()
                .Where(a => a.AttemptedAtUtc >= since)
                .GroupBy(a => a.AttemptedAtUtc.Date)
                .Select(g => new { Day = g.Key, Count = g.Count(), Last = g.Max(a => a.AttemptedAtUtc) })
                .ToListAsync(ct))
            .Select(g => (g.Day, g.Count, g.Last))
            .ToList();
        return new Data(now, enabled, dueSongs, dueAttempts, perDay);
    }

    public async Task<BackgroundJobSummaryDto> SummarizeAsync(CancellationToken ct)
        => Summarize(await LoadAsync(ct));

    private BackgroundJobSummaryDto Summarize(Data d)
        => BackgroundJobSummaries.Build(
            BackgroundJobDefinitions.EnrichmentRetries,
            d.Enabled.Count == 0 ? "Off: no enrichment provider is enabled." : null,
            status.Get(Id),
            d.AttemptsPerDay.Count == 0 ? null : d.AttemptsPerDay.Max(a => a.Last),
            new BackgroundHeadlineDto(d.DueSongs, null, BackgroundFormat.Count, "songs have a provider retry due now", null, null),
            TimeBuckets.DailyTotals(d.Now, TimeBuckets.TrendDays, d.AttemptsPerDay.Select(a => (a.Day, (double)a.Count))),
            "provider attempts per day");

    public async Task<BackgroundJobDetailDto> DescribeAsync(CancellationToken ct)
    {
        var d = await LoadAsync(ct);

        var firstHour = TimeBuckets.Starts(d.Now, hourly: true, HourlyBuckets)[0];
        var perHour = await db.SongProviderAttempts.AsNoTracking()
            .Where(a => a.AttemptedAtUtc >= firstHour)
            .GroupBy(a => new { a.AttemptedAtUtc.Date, a.AttemptedAtUtc.Hour, a.Status })
            .Select(g => new { g.Key.Date, g.Key.Hour, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);

        var byStatus = await db.Songs.AsNoTracking()
            .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic)
            .ExcludingDemoTenant()
            .GroupBy(s => s.EnrichmentStatus)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var waiting = await db.SongProviderAttempts.AsNoTracking()
            .WhereRetriesApply(d.Enabled)
            .WhereAwaitingRetry()
            .GroupBy(a => a.Provider)
            .Select(g => new { Provider = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var limited = db.SongProviderAttempts.AsNoTracking()
            .WhereRetriesApply(d.Enabled)
            .Where(a => a.Status == ProviderAttemptStatus.RateLimited);
        var limitedNow = await limited.CountAsync(ct);
        var stuckBefore = d.Now - StuckAfter;
        var stuck = await limited.CountAsync(a => a.RateLimitedSinceUtc < stuckBefore, ct);
        var oldestLimit = await limited.MinAsync(a => a.RateLimitedSinceUtc, ct);
        var cooling = await db.SongProviderAttempts.AsNoTracking()
            .WhereRetriesApply(d.Enabled)
            .CountAsync(a => (a.Status == ProviderAttemptStatus.NoMatch || a.Status == ProviderAttemptStatus.Failed)
                && a.NextRetryAfterUtc > d.Now, ct);

        var songSegments = new (EnrichmentStatus Status, string Label, string Tone)[]
        {
            (EnrichmentStatus.Matched, "Matched", BackgroundTone.Good),
            (EnrichmentStatus.NeedsReview, "Needs review", BackgroundTone.Warning),
            (EnrichmentStatus.Pending, "Waiting to be enriched", BackgroundTone.Pending),
            (EnrichmentStatus.Failed, "Failed", BackgroundTone.Bad),
        };
        var covered = songSegments.Select(s => s.Status).ToHashSet();
        var segments = songSegments
            .Select(s => new BackgroundPanels.Segment(
                s.Status.ToString(), s.Label, byStatus.Where(b => b.Status == s.Status).Sum(b => b.Count), s.Tone))
            .Append(new BackgroundPanels.Segment(
                "other", "Other", byStatus.Where(b => !covered.Contains(b.Status)).Sum(b => b.Count), BackgroundTone.Muted));

        var panels = new List<BackgroundPanelDto>
        {
            BackgroundPanels.Breakdown("Every song's enrichment status", null, "songs", segments),
            new BarsPanelDto(
                "Attempts waiting for a retry, by provider",
                "Rate-limited attempts, and attempts that found nothing or failed and have a cooldown running. Due or not.",
                BackgroundFormat.Count,
                [.. d.Enabled.OrderBy(p => p).Select(p => new BarDto(
                    ProviderLabel(p), waiting.Where(w => w.Provider == p).Sum(w => w.Count), BackgroundTone.Pending))]),
            new SeriesPanelDto(
                "Provider attempts per hour, last 48 hours",
                "Each song and provider keeps one attempt row, so an attempt counts on the hour of its latest try.",
                "hour", BackgroundFormat.Count, true,
                [.. Outcomes.Select(o => new SeriesDefinitionDto(o.Status.ToString(), o.Label, o.Tone))],
                TimeBuckets.Fill(
                    d.Now, hourly: true, HourlyBuckets, Outcomes.Length,
                    perHour.Select(a => (
                        DateTime.SpecifyKind(a.Date.AddHours(a.Hour), DateTimeKind.Utc),
                        Array.FindIndex(Outcomes, o => o.Status == a.Status),
                        (double)a.Count))),
                []),
            new FiguresPanelDto("Stuck loops", "A provider that keeps rate-limiting holds songs back for as long as it does.",
            [
                BackgroundPanels.Count(
                    "Retries due now", d.DueAttempts,
                    "attempts; the sweep skips a song whose provider no longer applies to it"),
                BackgroundPanels.Count("Rate limited right now", limitedNow),
                BackgroundPanels.Count(
                    "Rate limited for over a day", stuck, null, stuck > 0 ? BackgroundTone.Bad : BackgroundTone.Neutral),
                BackgroundPanels.Moment("Longest rate limit began", oldestLimit),
                BackgroundPanels.Count("Cooling down", cooling, "found nothing or failed; asked again later"),
            ]),
        };

        return new BackgroundJobDetailDto(Summarize(d), panels);
    }

    private static string ProviderLabel(EnrichmentProvider provider) => provider switch
    {
        EnrichmentProvider.AcoustID => "AcoustID",
        EnrichmentProvider.SpotifyAPI => "Spotify",
        EnrichmentProvider.MusicBrainzWeb => "MusicBrainz",
        EnrichmentProvider.AppleMusic => "Apple Music",
        EnrichmentProvider.YeTracker => "Ye tracker",
        _ => provider.ToString(),
    };
}
