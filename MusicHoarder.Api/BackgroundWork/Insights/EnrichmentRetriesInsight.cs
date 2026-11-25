using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.BackgroundWork.Charts;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.BackgroundWork.Insights;

/// <summary>
/// Provider attempts waiting to be asked again. The due attempts are
/// <see cref="EnrichmentQueries.WhereRetryableProviderAttempts"/>, the sweep's own query. The sweep then drops a
/// song whose provider can no longer handle it, a C# check no query can make, so the headline can run ahead
/// of what the sweep enqueues; the figure says so.
/// </summary>
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
        List<(DateTime At, ProviderAttemptStatus Status)> Attempts);

    private async Task<Data> LoadAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var enabled = await orchestrator.GetEnabledProviderEnumsAsync(ct);
        var due = await db.SongProviderAttempts.AsNoTracking()
            .WhereRetryableProviderAttempts(now, enabled)
            .Select(a => a.SongId)
            .ToListAsync(ct);
        var since = now.AddDays(-TimeBuckets.TrendDays);
        var attempts = (await db.SongProviderAttempts.AsNoTracking()
                .Where(a => a.AttemptedAtUtc > since)
                .Select(a => new { a.AttemptedAtUtc, a.Status })
                .ToListAsync(ct))
            .Select(a => (a.AttemptedAtUtc, a.Status))
            .ToList();
        return new Data(now, enabled, due.Distinct().Count(), due.Count, attempts);
    }

    public async Task<BackgroundJobSummaryDto> SummarizeAsync(CancellationToken ct)
        => Summarize(await LoadAsync(ct));

    private BackgroundJobSummaryDto Summarize(Data d)
        => BackgroundJobSummaries.Build(
            BackgroundJobDefinitions.EnrichmentRetries,
            d.Enabled.Count == 0 ? "Off: no enrichment provider is enabled." : null,
            status.Get(Id),
            d.Attempts.Count == 0 ? null : d.Attempts.Max(a => a.At),
            new BackgroundHeadlineDto(d.DueSongs, null, BackgroundFormat.Count, "songs have a provider retry due now", null, null),
            TimeBuckets.DailyCounts(d.Now, TimeBuckets.TrendDays, d.Attempts.Select(a => a.At)),
            "provider attempts per day");

    public async Task<BackgroundJobDetailDto> DescribeAsync(CancellationToken ct)
    {
        var d = await LoadAsync(ct);

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
                    d.Attempts.Select(a => (a.At, Array.FindIndex(Outcomes, o => o.Status == a.Status), 1.0))),
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
