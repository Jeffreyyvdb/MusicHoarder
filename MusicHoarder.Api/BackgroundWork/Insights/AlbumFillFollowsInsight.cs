using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.BackgroundWork.Charts;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Settings;

namespace MusicHoarder.Api.BackgroundWork.Insights;

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

    private sealed record Group(
        WishlistItemOrigin Origin, WishlistItemStatus Status, bool RetryScheduled, int Count,
        DateTime FirstCreatedAtUtc, DateTime LastCreatedAtUtc, DateTime LastUpdatedAtUtc, int UpdatedThisWeek)
    {
        public bool IsWaiting => Status is WishlistItemStatus.Pending or WishlistItemStatus.Downloading
            || (Status == WishlistItemStatus.Failed && RetryScheduled);
    }

    private sealed record Data(DateTime Now, List<Group> Groups, List<DateTime> DownloadedRecently, string? OffReason)
    {
        public int Total => Groups.Sum(g => g.Count);

        public int Count(WishlistItemOrigin origin, Func<Group, bool> holds)
            => Groups.Where(g => g.Origin == origin && holds(g)).Sum(g => g.Count);
    }

    private IQueryable<WishlistItem> Queued()
        => db.WishlistItems.AsNoTracking()
            .Where(w => w.Origin == WishlistItemOrigin.AlbumCompletion || w.Origin == WishlistItemOrigin.ArtistMonitor);

    private async Task<Data> LoadAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var weekAgo = now.AddDays(-RateWindowDays);
        var groups = await Queued()
            .GroupBy(w => new { w.Origin, w.Status, RetryScheduled = w.NextAttemptAtUtc != null })
            .Select(g => new Group(
                g.Key.Origin, g.Key.Status, g.Key.RetryScheduled, g.Count(),
                g.Min(w => w.CreatedAtUtc), g.Max(w => w.CreatedAtUtc), g.Max(w => w.UpdatedAtUtc),
                g.Count(w => w.UpdatedAtUtc > weekAgo)))
            .ToListAsync(ct);

        var since = TimeBuckets.Starts(now, hourly: false, TimeBuckets.TrendDays)[0];
        var downloaded = await Queued()
            .Where(w => w.Status == WishlistItemStatus.Downloaded && w.UpdatedAtUtc >= since)
            .Select(w => w.UpdatedAtUtc)
            .ToListAsync(ct);

        var opts = options.CurrentValue;
        var fillOn = opts.EnableWishlistDownloads && (await runtimeSettings.GetAsync(ct)).AlbumCompletionEnabled;
        var off = !fillOn && !opts.EnableArtistMonitor
            ? "Off: album completion and the artist monitor are both switched off."
            : null;
        return new Data(now, groups, downloaded, off);
    }

    public async Task<BackgroundJobSummaryDto> SummarizeAsync(CancellationToken ct)
        => Summarize(await LoadAsync(ct));

    private BackgroundJobSummaryDto Summarize(Data d)
    {
        var downloaded = d.Groups.Where(g => g.Status == WishlistItemStatus.Downloaded).ToList();
        var waiting = d.Groups.Where(g => g.IsWaiting).Sum(g => g.Count);
        var rate = EtaEstimator.RatePerDay(
            downloaded.Sum(g => g.UpdatedThisWeek), d.Groups.Count == 0 ? null : d.Groups.Min(g => g.FirstCreatedAtUtc),
            d.Now, RateWindowDays);

        var lastWorked = d.Groups.Select(g => (DateTime?)g.LastCreatedAtUtc)
            .Concat(downloaded.Select(g => (DateTime?)g.LastUpdatedAtUtc))
            .Max();

        return BackgroundJobSummaries.Build(
            BackgroundJobDefinitions.AlbumFillAndFollows,
            d.OffReason,
            status.Get(Id),
            lastWorked,
            new BackgroundHeadlineDto(
                waiting, d.Total, BackgroundFormat.Count,
                $"of {BackgroundText.Count(d.Total)} queued tracks are still waiting",
                EtaEstimator.Eta(waiting, rate, d.Now),
                rate > 0 ? $"at the last {RateWindowDays} days' rate of {BackgroundText.Count(rate)} downloads a day" : null),
            TimeBuckets.DailyCounts(d.Now, TimeBuckets.TrendDays, d.DownloadedRecently),
            "queued tracks downloaded per day");
    }

    public async Task<BackgroundJobDetailDto> DescribeAsync(CancellationToken ct)
    {
        var d = await LoadAsync(ct);
        var monthAgo = d.Now.AddDays(-DailyBuckets);

        var segments = new List<BackgroundPanels.Segment>();
        foreach (var (origin, key, label) in Origins)
        {
            segments.Add(new($"{key}-pending", $"{label} · waiting", d.Count(origin, g => g.Status is WishlistItemStatus.Pending or WishlistItemStatus.Downloading), BackgroundTone.Pending));
            segments.Add(new($"{key}-retrying", $"{label} · failed, retrying", d.Count(origin, g => g.Status == WishlistItemStatus.Failed && g.RetryScheduled), BackgroundTone.Warning));
            segments.Add(new($"{key}-downloaded", $"{label} · downloaded", d.Count(origin, g => g.Status == WishlistItemStatus.Downloaded), BackgroundTone.Good));
            segments.Add(new($"{key}-parked", $"{label} · failed for good", d.Count(origin, g => g.Status == WishlistItemStatus.Failed && !g.RetryScheduled), BackgroundTone.Bad,
                "Out of attempts; it waits for a manual retry."));
            segments.Add(new($"{key}-notfound", $"{label} · not found", d.Count(origin, g => g.Status == WishlistItemStatus.NotFound), BackgroundTone.Muted));
            segments.Add(new($"{key}-owned", $"{label} · already owned", d.Count(origin, g => g.Status == WishlistItemStatus.SkippedOwned), BackgroundTone.Muted));
        }

        var firstDay = TimeBuckets.Starts(d.Now, hourly: false, DailyBuckets)[0];
        var queuedThisMonth = await Queued()
            .Where(w => w.CreatedAtUtc >= firstDay)
            .Select(w => new { w.Origin, w.CreatedAtUtc })
            .ToListAsync(ct);
        var downloadedThisMonth = await Queued()
            .Where(w => w.Status == WishlistItemStatus.Downloaded && w.UpdatedAtUtc >= firstDay)
            .Select(w => new { w.Origin, w.UpdatedAtUtc })
            .ToListAsync(ct);

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
                    queuedThisMonth.Select(i => (i.CreatedAtUtc, OriginIndex(i.Origin), 1.0))),
                []),
            new SeriesPanelDto(
                "Tracks downloaded per day, last 30 days",
                "A downloaded track counts on the day its row last changed.",
                "day", BackgroundFormat.Count, true, seriesDefs,
                TimeBuckets.Fill(d.Now, hourly: false, DailyBuckets, Origins.Length,
                    downloadedThisMonth.Select(i => (i.UpdatedAtUtc, OriginIndex(i.Origin), 1.0))),
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
