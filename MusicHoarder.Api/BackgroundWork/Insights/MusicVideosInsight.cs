using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.BackgroundWork.Charts;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Settings;

namespace MusicHoarder.Api.BackgroundWork.Insights;

public sealed class MusicVideosInsight(
    MusicHoarderDbContext db,
    MusicVideoBacklogLoader backlog,
    IRuntimeSettingsService runtimeSettings,
    IOptionsMonitor<MusicEnricherOptions> options,
    BackgroundWorkStatus status,
    TimeProvider time) : IBackgroundJobInsight
{
    private const int DailyBuckets = 30;
    private const int ForwardDays = 30;
    private const int RateWindowDays = 7;

    public string Id => BackgroundJobIds.MusicVideos;

    /// <summary>Songs in scope whose last search fell on one UTC day, split at the start of the rate window.</summary>
    private sealed record SearchDay(DateTime Day, bool InRateWindow, int Songs, DateTime First, DateTime Last);

    private sealed record Counts(
        DateTime Now, MusicEnricherOptions Options, string? OffReason, int InScope, List<SearchDay> Days)
    {
        public int Searched => Days.Sum(d => d.Songs);
        public int NeverSearched => InScope - Searched;
        public DateTime? LastSearchedAtUtc => Days.Count == 0 ? null : Days.Max(d => d.Last);

        public double RatePerDay
        {
            get
            {
                var window = Days.Where(d => d.InRateWindow).ToList();
                return EtaEstimator.RatePerDay(
                    window.Sum(d => d.Songs), window.Count == 0 ? null : window.Min(d => d.First), Now, RateWindowDays);
            }
        }
    }

    private async Task<Counts> LoadAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var opts = options.CurrentValue;
        var rateWindowStart = now.AddDays(-RateWindowDays);

        var scope = MusicVideoPrefetchScope.InPrefetchScope(db);
        var inScope = await scope.CountAsync(ct);
        var days = await db.SongVideoCandidateSets.IgnoreQueryFilters().AsNoTracking()
            .Where(c => scope.Select(s => s.Id).Contains(c.SongId))
            .GroupBy(c => new { c.SearchedAtUtc.Date, InRateWindow = c.SearchedAtUtc > rateWindowStart })
            .Select(g => new SearchDay(
                g.Key.Date, g.Key.InRateWindow, g.Count(), g.Min(c => c.SearchedAtUtc), g.Max(c => c.SearchedAtUtc)))
            .ToListAsync(ct);

        return new Counts(now, opts, await OffReasonAsync(opts, ct), inScope, days);
    }

    private async Task<string?> OffReasonAsync(MusicEnricherOptions opts, CancellationToken ct)
    {
        if (opts.MusicVideoCandidatePrefetchIntervalMinutes <= 0)
            return "Switched off in the configuration (MusicVideoCandidatePrefetchIntervalMinutes is 0).";

        var automatic = (await runtimeSettings.GetAsync(ct)).AutoDownloadMusicVideos;
        if (!await MusicVideoPrefetchScope.HasStartedAsync(db, automatic, ct))
            return "Not started: searches begin once a music video has been fetched or automatic downloads are turned on.";
        return null;
    }

    public async Task<BackgroundJobSummaryDto> SummarizeAsync(CancellationToken ct)
        => Summarize(await LoadAsync(ct));

    private BackgroundJobSummaryDto Summarize(Counts d)
    {
        var rate = d.RatePerDay;
        var never = d.NeverSearched;
        var eta = d.OffReason is null ? EtaEstimator.Eta(never, rate, d.Now) : null;
        string? etaLabel = null;
        if (never > 0)
        {
            etaLabel = rate > 0
                ? $"First pass through the library: {BackgroundText.Count(never)} songs never searched, at the last {RateWindowDays} days' rate of {BackgroundText.Count(rate)} searches a day."
                : $"First pass through the library: {BackgroundText.Count(never)} songs never searched, and no searches in the last {RateWindowDays} days to estimate a rate from.";
        }

        return BackgroundJobSummaries.Build(
            BackgroundJobDefinitions.MusicVideos,
            d.OffReason,
            status.Get(Id),
            d.LastSearchedAtUtc,
            new BackgroundHeadlineDto(
                d.Searched, d.InScope, BackgroundFormat.Count,
                $"of {BackgroundText.Count(d.InScope)} songs have been searched for a music video", eta, etaLabel),
            TimeBuckets.DailyTotals(
                d.Now, TimeBuckets.TrendDays, d.Days.Select(x => (x.Day, (double)x.Songs))),
            "songs searched per day");
    }

    private static readonly SeriesDefinitionDto[] OutcomeSeries =
    [
        new("clip", "A confident pick", BackgroundTone.Neutral),
        new("no-clip", "Candidates, none confident", BackgroundTone.Muted),
        new("empty", "Found nothing, or failed", BackgroundTone.Warning),
    ];

    private static int SeriesOf(MusicVideoSearchOutcome outcome) => outcome switch
    {
        MusicVideoSearchOutcome.Clip => 0,
        MusicVideoSearchOutcome.NoClip => 1,
        _ => 2,
    };

    public async Task<BackgroundJobDetailDto> DescribeAsync(CancellationToken ct)
    {
        var d = await LoadAsync(ct);
        var b = await backlog.LoadAsync(ct);
        var opts = d.Options;
        var inScope = b.Songs.Where(s => s.InScope).ToList();
        var searchedInScope = inScope.Where(s => s.Set is not null).ToList();

        long Outcome(MusicVideoSearchOutcome outcome)
            => searchedInScope.Count(s => s.Verdict!.Outcome == outcome);

        var breakdown = BackgroundPanels.Breakdown(
            "What the last search found",
            "Every song the search considers, counted once: by the video it already has, or by what its most recent search found.",
            "songs",
            [
                new("video-automatic", "Has a video, downloaded automatically",
                    b.Songs.Count(s => s.WorkingVideoPick == MusicVideoPick.Automatic), BackgroundTone.Good),
                new("video-requested", "Has a video, picked by a person",
                    b.Songs.Count(s => s.WorkingVideoPick == MusicVideoPick.Requested), BackgroundTone.Good),
                new("clip", "A confident pick", Outcome(MusicVideoSearchOutcome.Clip), BackgroundTone.Neutral,
                    "A candidate passes every check a download needs; it waits for its download."),
                new("no-clip", "Candidates, none confident", Outcome(MusicVideoSearchOutcome.NoClip), BackgroundTone.Muted,
                    "A person can still choose one from the list."),
                new("empty", "Found nothing, or failed", Outcome(MusicVideoSearchOutcome.Empty), BackgroundTone.Warning,
                    "Searched again on a back-off that doubles with each empty search."),
                new("never", "Not searched yet", d.NeverSearched, BackgroundTone.Pending),
            ]);

        var items = b.Songs
            .Select(s => new MapItem(s.AddedAt, !s.InScope ? MapState.Excluded : s.Set is null ? MapState.Waiting : MapState.Done))
            .ToList();
        var markers = new List<LibraryMapMarkerDto>();
        if (LibraryMapBinner.WaitingFrontier(items) is { } frontier)
            markers.Add(new("Next in line, newest first", frontier.Newest));
        var map = new LibraryMapPanelDto(
            "Where the search has reached",
            "The songs the search considers, oldest addition on the left. The pass takes the newest first, so the searched stretch grows from the right.",
            "Searched", "Not searched yet", "Has a video",
            LibraryMapBinner.Bin(items), markers);

        var searchedByDay = TimeBuckets.Fill(
            b.Now, hourly: false, DailyBuckets, OutcomeSeries.Length,
            b.Songs.Where(s => s.Set is not null).Select(s => (s.Set!.SearchedAtUtc, SeriesOf(s.Verdict!.Outcome), 1.0)));
        var searches = OutcomeSeriesPanel(
            "Searches per day, last 30 days",
            "A song counts on the day of its latest search, so a day that was later searched again reads lower than it was.",
            searchedByDay);

        var coming = OutcomeSeriesPanel(
            "Searches coming due, next 30 days",
            "When each searched song is searched again, by what its last search found: a clip every 30 or 180 days, no clip every 7 or 120, nothing found on a back-off from 1 to 180. Searches already due are counted on today.",
            TimeBuckets.FillForward(
                b.Now, ForwardDays, OutcomeSeries.Length,
                searchedInScope.Select(s => (s.Due ? b.Now : s.Set!.NextSearchAtUtc!.Value, SeriesOf(s.Verdict!.Outcome), 1.0))));

        var depth = DepthBars(searchedInScope);

        var due = inScope.Count(s => s.Due);
        var dueAgain = inScope.Count(s => s.Due && s.Set is not null);
        var later = searchedInScope.Count(s => !s.Due && s.Set!.NextSearchAtUtc!.Value >= b.Now.Date.AddDays(ForwardDays));
        var failed = searchedInScope.Count(s => s.Set!.SearchErrored);
        var perPassPerDay = opts.MusicVideoCandidatePrefetchIntervalMinutes > 0
            ? (double)opts.MusicVideoCandidatePrefetchBatchSize * 1440 / opts.MusicVideoCandidatePrefetchIntervalMinutes
            : 0;
        var figures = new FiguresPanelDto("The search in numbers", null,
        [
            BackgroundPanels.Count("Songs in scope", inScope.Count, "no video yet, or a failed one"),
            BackgroundPanels.Count("Searched", d.Searched, "have a stored list"),
            BackgroundPanels.Count(
                "Due now", due,
                $"{BackgroundText.Count(due - dueAgain)} never searched, {BackgroundText.Count(dueAgain)} coming round again",
                due > 0 ? BackgroundTone.Pending : BackgroundTone.Neutral),
            BackgroundPanels.Count(
                "Per pass", opts.MusicVideoCandidatePrefetchBatchSize,
                opts.MusicVideoCandidatePrefetchIntervalMinutes > 0
                    ? $"songs every {BackgroundText.Count(opts.MusicVideoCandidatePrefetchIntervalMinutes)} min, at most {BackgroundText.Count(perPassPerDay)} a day"
                    : "the search is switched off"),
            BackgroundPanels.Count(
                "Failed searches", failed,
                "YouTube or yt-dlp errored; tried again on the back-off",
                failed > 0 ? BackgroundTone.Warning : BackgroundTone.Neutral),
            BackgroundPanels.Count("Due after 30 days", later, "beyond the chart above"),
        ]);

        return new BackgroundJobDetailDto(Summarize(d), [breakdown, map, searches, coming, depth, figures]);
    }

    private static SeriesPanelDto OutcomeSeriesPanel(string title, string caption, IReadOnlyList<SeriesPointDto> points)
        => new(title, caption, "day", BackgroundFormat.Count, true, OutcomeSeries, points, []);

    private static BarsPanelDto DepthBars(IEnumerable<BacklogSong> searched)
    {
        (string Label, Func<int, bool> Holds, string Tone)[] bands =
        [
            ("0", n => n == 0, BackgroundTone.Muted),
            ("1", n => n == 1, BackgroundTone.Pending),
            ("2", n => n == 2, BackgroundTone.Pending),
            ("3", n => n == 3, BackgroundTone.Pending),
            ("4 to 5", n => n is 4 or 5, BackgroundTone.Pending),
            ("6 or more", n => n >= 6, BackgroundTone.Warning),
        ];
        var empty = searched.Where(s => s.Verdict!.Outcome == MusicVideoSearchOutcome.Empty).ToList();
        return new BarsPanelDto(
            "Empty searches in a row",
            "Songs whose last search found nothing or failed, by how many searches in a row did. Each doubles the wait, up to 14 days for a recent release and 180 for an older one. 0 is a record from before the count was kept.",
            BackgroundFormat.Count,
            [.. bands.Select(band => new BarDto(band.Label, empty.Count(s => band.Holds(s.Set!.EmptySearches)), band.Tone))]);
    }
}
