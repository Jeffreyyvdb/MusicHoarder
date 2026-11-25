using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.BackgroundWork.Charts;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Settings;

namespace MusicHoarder.Api.BackgroundWork.Insights;

/// <summary>
/// The tracks album completion and the artist monitor put on the wishlist, and the follows themselves. Both
/// jobs end in <see cref="WishlistItem"/> rows, so what they queued and what became of it is read from those
/// rows by <see cref="WishlistItem.Origin"/>.
/// </summary>
public sealed class AlbumFillFollowsInsight(
    MusicHoarderDbContext db,
    IOptionsMonitor<MusicEnricherOptions> options,
    IRuntimeSettingsService runtimeSettings,
    BackgroundWorkStatus status,
    TimeProvider time) : IBackgroundJobInsight
{
    private const int DailyBuckets = 30;
    private const int RateWindowDays = 7;

    private static readonly (WishlistItemOrigin Origin, string Key, string Label)[] Origins =
    [
        (WishlistItemOrigin.AlbumCompletion, "album-completion", "Album completion"),
        (WishlistItemOrigin.ArtistMonitor, "artist-monitor", "Followed artists"),
    ];

    public string Id => BackgroundJobIds.AlbumFillAndFollows;

    private sealed record Item(
        WishlistItemOrigin Origin, WishlistItemStatus Status, DateTime? NextAttemptAtUtc, DateTime CreatedAtUtc, DateTime UpdatedAtUtc)
    {
        /// <summary>A failed item with a retry on the calendar is still in flight; one without is parked.</summary>
        public bool IsWaiting => Status is WishlistItemStatus.Pending or WishlistItemStatus.Downloading
            || (Status == WishlistItemStatus.Failed && NextAttemptAtUtc is not null);
    }

    private sealed record Data(DateTime Now, List<Item> Items, string? OffReason);

    private async Task<Data> LoadAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var items = (await db.WishlistItems.AsNoTracking()
                .Where(w => w.Origin == WishlistItemOrigin.AlbumCompletion || w.Origin == WishlistItemOrigin.ArtistMonitor)
                .Select(w => new { w.Origin, w.Status, w.NextAttemptAtUtc, w.CreatedAtUtc, w.UpdatedAtUtc })
                .ToListAsync(ct))
            .Select(w => new Item(w.Origin, w.Status, w.NextAttemptAtUtc, w.CreatedAtUtc, w.UpdatedAtUtc))
            .ToList();

        var opts = options.CurrentValue;
        var fillOn = opts.EnableWishlistDownloads && (await runtimeSettings.GetAsync(ct)).AlbumCompletionEnabled;
        var off = !fillOn && !opts.EnableArtistMonitor
            ? "Off: album completion and the artist monitor are both switched off."
            : null;
        return new Data(now, items, off);
    }

    public async Task<BackgroundJobSummaryDto> SummarizeAsync(CancellationToken ct)
        => Summarize(await LoadAsync(ct));

    private BackgroundJobSummaryDto Summarize(Data d)
    {
        var downloaded = d.Items.Where(i => i.Status == WishlistItemStatus.Downloaded).ToList();
        var waiting = d.Items.Count(i => i.IsWaiting);
        var recent = downloaded.Where(i => i.UpdatedAtUtc > d.Now.AddDays(-RateWindowDays)).ToList();
        var rate = EtaEstimator.RatePerDay(
            recent.Count, d.Items.Count == 0 ? null : d.Items.Min(i => i.CreatedAtUtc), d.Now, RateWindowDays);

        var lastWorked = d.Items.Count == 0
            ? (DateTime?)null
            : new[] { d.Items.Max(i => i.CreatedAtUtc), downloaded.Count == 0 ? DateTime.MinValue : downloaded.Max(i => i.UpdatedAtUtc) }.Max();

        return BackgroundJobSummaries.Build(
            BackgroundJobDefinitions.AlbumFillAndFollows,
            d.OffReason,
            status.Get(Id),
            lastWorked,
            new BackgroundHeadlineDto(
                waiting, d.Items.Count, BackgroundFormat.Count,
                $"of {BackgroundText.Count(d.Items.Count)} queued tracks are still waiting",
                EtaEstimator.Eta(waiting, rate, d.Now),
                rate > 0 ? $"at the last {RateWindowDays} days' rate of {BackgroundText.Count(rate)} downloads a day" : null),
            TimeBuckets.DailyCounts(d.Now, TimeBuckets.TrendDays, downloaded.Select(i => i.UpdatedAtUtc)),
            "queued tracks downloaded per day");
    }

    public async Task<BackgroundJobDetailDto> DescribeAsync(CancellationToken ct)
    {
        var d = await LoadAsync(ct);
        var monthAgo = d.Now.AddDays(-DailyBuckets);

        var segments = new List<BackgroundPanels.Segment>();
        foreach (var (origin, key, label) in Origins)
        {
            var mine = d.Items.Where(i => i.Origin == origin).ToList();
            segments.Add(new($"{key}-pending", $"{label} · waiting", mine.Count(i => i.Status is WishlistItemStatus.Pending or WishlistItemStatus.Downloading), BackgroundTone.Pending));
            segments.Add(new($"{key}-retrying", $"{label} · failed, retrying", mine.Count(i => i.Status == WishlistItemStatus.Failed && i.NextAttemptAtUtc is not null), BackgroundTone.Warning));
            segments.Add(new($"{key}-downloaded", $"{label} · downloaded", mine.Count(i => i.Status == WishlistItemStatus.Downloaded), BackgroundTone.Good));
            segments.Add(new($"{key}-parked", $"{label} · failed for good", mine.Count(i => i.Status == WishlistItemStatus.Failed && i.NextAttemptAtUtc is null), BackgroundTone.Bad,
                "Out of attempts; it waits for a manual retry."));
            segments.Add(new($"{key}-notfound", $"{label} · not found", mine.Count(i => i.Status == WishlistItemStatus.NotFound), BackgroundTone.Muted));
            segments.Add(new($"{key}-owned", $"{label} · already owned", mine.Count(i => i.Status == WishlistItemStatus.SkippedOwned), BackgroundTone.Muted));
        }

        var seriesDefs = Origins.Select(o => new SeriesDefinitionDto(o.Key, o.Label, o.Key == "album-completion" ? BackgroundTone.Pending : BackgroundTone.Ai)).ToList();
        int OriginIndex(WishlistItemOrigin origin) => Array.FindIndex(Origins, o => o.Origin == origin);

        var follows = await db.FollowedArtists.AsNoTracking()
            .Select(f => new { f.LastCheckedAtUtc, f.NextCheckAtUtc, HasError = f.LastError != null })
            .ToListAsync(ct);
        var found = await db.FollowedArtistReleases.AsNoTracking()
            .CountAsync(r => FollowedReleaseDecisions.News.Contains(r.Decision) && r.DecidedAtUtc > monthAgo, ct);

        var panels = new List<BackgroundPanelDto>
        {
            BackgroundPanels.Breakdown("What became of the queued tracks", "Every track album completion or a follow put on the wishlist.", "tracks", segments),
            new SeriesPanelDto(
                "Tracks queued per day, last 30 days", null, "day", BackgroundFormat.Count, true, seriesDefs,
                TimeBuckets.Fill(d.Now, hourly: false, DailyBuckets, Origins.Length,
                    d.Items.Select(i => (i.CreatedAtUtc, OriginIndex(i.Origin), 1.0))),
                []),
            new SeriesPanelDto(
                "Tracks downloaded per day, last 30 days",
                "A downloaded track counts on the day its row last changed.",
                "day", BackgroundFormat.Count, true, seriesDefs,
                TimeBuckets.Fill(d.Now, hourly: false, DailyBuckets, Origins.Length,
                    d.Items.Where(i => i.Status == WishlistItemStatus.Downloaded).Select(i => (i.UpdatedAtUtc, OriginIndex(i.Origin), 1.0))),
                []),
            new FiguresPanelDto("Followed artists", null,
            [
                BackgroundPanels.Count("Followed artists", follows.Count),
                BackgroundPanels.Moment("Last check", follows.Max(f => f.LastCheckedAtUtc), follows.Count == 0 ? "No artist is followed." : null),
                BackgroundPanels.Moment("Next check due", follows.Min(f => f.NextCheckAtUtc)),
                BackgroundPanels.Count(
                    "Checks that failed last time", follows.Count(f => f.HasError), "tried again soon",
                    follows.Any(f => f.HasError) ? BackgroundTone.Warning : BackgroundTone.Neutral),
                BackgroundPanels.Count("New releases found, last 30 days", found),
            ]),
        };

        return new BackgroundJobDetailDto(Summarize(d), panels);
    }
}
