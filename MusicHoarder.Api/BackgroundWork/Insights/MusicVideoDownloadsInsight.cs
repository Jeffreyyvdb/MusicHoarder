using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.BackgroundWork.Charts;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Settings;
using MusicHoarder.Api.Storage;

namespace MusicHoarder.Api.BackgroundWork.Insights;

public sealed class MusicVideoDownloadsInsight(
    MusicHoarderDbContext db,
    MusicVideoBacklogLoader backlog,
    IRuntimeSettingsService runtimeSettings,
    IOptionsMonitor<MusicEnricherOptions> options,
    IFreeDiskSpace disk,
    StorageUsageSnapshotStore storage,
    BackgroundWorkStatus status,
    TimeProvider time) : IBackgroundJobInsight
{
    private const int DailyBuckets = 30;
    private const long Gib = 1024L * 1024 * 1024;

    public string Id => BackgroundJobIds.MusicVideoDownloads;

    private sealed record Data(
        DateTime Now,
        MusicEnricherOptions Options,
        bool Automatic,
        long? FreeBytes,
        long FloorBytes,
        long? ClipsOnDiskBytes,
        Dictionary<(MusicVideoStatus Status, MusicVideoPick Pick), int> Rows,
        List<(DateTime At, MusicVideoPick Pick)> Ready)
    {
        public int Of(MusicVideoStatus s) => Rows.Where(r => r.Key.Status == s).Sum(r => r.Value);
        public int Of(MusicVideoStatus s, MusicVideoPick p) => Rows.GetValueOrDefault((s, p));
        public bool BelowFloor => FreeBytes is not { } free || free < FloorBytes;
    }

    private async Task<Data> LoadAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var opts = options.CurrentValue;
        var videos = MusicVideoPrefetchScope.VideosOfEligible(db);

        var rows = (await videos
            .GroupBy(v => new { v.Status, v.Pick })
            .Select(g => new { g.Key.Status, g.Key.Pick, Count = g.Count() })
            .ToListAsync(ct)).ToDictionary(g => (g.Status, g.Pick), g => g.Count);

        var since = now.AddDays(-DailyBuckets);
        var ready = (await videos
            .Where(v => v.Status == MusicVideoStatus.Ready && v.FetchedAtUtc > since)
            .Select(v => new { v.FetchedAtUtc, v.Pick })
            .ToListAsync(ct)).Select(v => (v.FetchedAtUtc, v.Pick)).ToList();

        var automatic = (await runtimeSettings.GetAsync(ct)).AutoDownloadMusicVideos;
        var free = disk.AvailableBytes(MusicVideoDownloader.ResolveVideoDirectory(opts));
        long? clips = storage.Get().Snapshot?.Categories.FirstOrDefault(c => c.Key == CategoryKeys.Videos)?.Bytes;

        return new Data(now, opts, automatic, free, opts.MusicVideoAutoDownloadMinFreeGb * Gib, clips, rows, ready);
    }

    public async Task<BackgroundJobSummaryDto> SummarizeAsync(CancellationToken ct)
        => Summarize(await LoadAsync(ct));

    private BackgroundJobSummaryDto Summarize(Data d)
    {
        var runtime = status.Get(Id);
        string? off = null;
        if (!d.Automatic)
            off = "Automatic downloads are off, so a video is only fetched when someone picks one.";
        else if (d.BelowFloor && runtime?.State is not (BackgroundJobState.Working or BackgroundJobState.Failing))
        {
            var reason = d.FreeBytes is { } free
                ? $"Waiting for disk space: {BackgroundText.Bytes(free)} free on the videos drive, and the floor is {BackgroundText.Bytes(d.FloorBytes)}."
                : "Waiting: the free space on the videos drive cannot be read, so nothing is queued automatically.";
            runtime = new JobRuntime(BackgroundJobState.Waiting, reason, null, null, d.Now, runtime?.LastWorkedAtUtc);
        }

        var ready = d.Of(MusicVideoStatus.Ready);
        var caption = d.ClipsOnDiskBytes is > 0 and var bytes
            ? $"music videos are ready to play, {BackgroundText.Bytes(bytes)} on disk"
            : "music videos are ready to play";
        return BackgroundJobSummaries.Build(
            BackgroundJobDefinitions.MusicVideoDownloads,
            off,
            runtime,
            d.Ready.Count == 0 ? null : d.Ready.Max(r => r.At),
            new BackgroundHeadlineDto(ready, null, BackgroundFormat.Count, caption, null, null),
            TimeBuckets.DailyCounts(d.Now, TimeBuckets.TrendDays, d.Ready.Select(r => r.At)),
            "videos fetched per day");
    }

    public async Task<BackgroundJobDetailDto> DescribeAsync(CancellationToken ct)
    {
        var d = await LoadAsync(ct);
        var b = await backlog.LoadAsync(ct);
        var opts = d.Options;

        var fetched = new SeriesPanelDto(
            "Videos fetched per day, last 30 days",
            "A video counts on the day it was last fetched. A failed fetch carries no date, so failures are in the counts below, not on this chart.",
            "day", BackgroundFormat.Count, true,
            [
                new("automatic", "Downloaded automatically", BackgroundTone.Good),
                new("requested", "Picked by a person", BackgroundTone.Good),
            ],
            TimeBuckets.Fill(
                d.Now, hourly: false, DailyBuckets, 2,
                d.Ready.Select(r => (r.At, r.Pick == MusicVideoPick.Automatic ? 0 : 1, 1.0))),
            []);

        var states = BackgroundPanels.Breakdown(
            "Where every video stands",
            "One row per song the search considers that has a video row.",
            "videos",
            [
                new("ready-automatic", "Ready, downloaded automatically",
                    d.Of(MusicVideoStatus.Ready, MusicVideoPick.Automatic), BackgroundTone.Good),
                new("ready-requested", "Ready, picked by a person",
                    d.Of(MusicVideoStatus.Ready, MusicVideoPick.Requested), BackgroundTone.Good),
                new("fetching", "Downloading", d.Of(MusicVideoStatus.Fetching), BackgroundTone.Pending),
                new("failed", "Failed", d.Of(MusicVideoStatus.Failed), BackgroundTone.Bad,
                    "Waits for a person: the automatic download never retries a song that has a row."),
            ]);

        var waiting = b.Songs
            .Where(s => s.Set is not null
                && MusicVideoPrefetchScope.IsWaitingStoredPickCandidate(
                    s.InScope, s.HasVideoRow, s.Due, s.Set.HasError, s.Set.EmptySearches)
                && s.Verdict is { Outcome: MusicVideoSearchOutcome.Clip })
            .ToList();
        var waitingBytes = waiting.Sum(s => s.Verdict!.PickBytes ?? 0);
        var floorLabel = $"the floor is {BackgroundText.Bytes(d.FloorBytes)}";

        var figures = new FiguresPanelDto("Downloads in numbers", null,
        [
            BackgroundPanels.Count(
                "Confident picks waiting", waiting.Count,
                waiting.Count == 0 ? "nothing is waiting"
                    : d.Automatic ? $"{BackgroundText.Bytes(waitingBytes)} to download"
                    : $"{BackgroundText.Bytes(waitingBytes)}, held back: automatic downloads are off",
                waiting.Count > 0 ? BackgroundTone.Pending : BackgroundTone.Neutral),
            BackgroundPanels.Bytes(
                "Free on the videos drive", d.FreeBytes,
                d.FreeBytes is null ? "cannot be read, so nothing is queued automatically" : floorLabel,
                d.BelowFloor ? BackgroundTone.Warning : BackgroundTone.Neutral),
            d.ClipsOnDiskBytes is { } onDisk
                ? BackgroundPanels.Bytes("Clips on disk", onDisk, "from the last storage scan")
                : BackgroundPanels.Bytes("Clips on disk", null, "the storage scan has not run yet"),
            BackgroundPanels.Count(
                "Failed videos", d.Of(MusicVideoStatus.Failed), "waiting for a person",
                d.Of(MusicVideoStatus.Failed) > 0 ? BackgroundTone.Bad : BackgroundTone.Neutral),
            BackgroundPanels.Bytes(
                "Size limit per clip", opts.MusicVideoAutoDownloadMaxClipMb * 1024.0 * 1024,
                $"and a score of at least {opts.MusicVideoAutoDownloadMinScore}"),
        ]);

        return new BackgroundJobDetailDto(Summarize(d), [fetched, states, figures, MissBars(b)]);
    }

    private static BarsPanelDto MissBars(MusicVideoBacklog b)
    {
        var bar = b.Options.MusicVideoAutoDownloadMinScore;
        var tops = b.Songs
            .Where(s => s.InScope && s.Verdict is { Outcome: MusicVideoSearchOutcome.NoClip, TopScore: not null })
            .Select(s => s.Verdict!.TopScore!.Value)
            .ToList();

        var edges = new[] { bar - 40, bar - 20, bar }.Where(e => e > 0).Distinct().Order().ToList();
        var bars = new List<BarDto>();
        var from = int.MinValue;
        foreach (var edge in edges)
        {
            var low = from;
            var label = from == int.MinValue ? $"Under {edge}" : $"{from} to {edge - 1}";
            bars.Add(new BarDto(label, tops.Count(t => t >= low && t < edge), BackgroundTone.Neutral));
            from = edge;
        }
        var floor = from;
        bars.Add(new BarDto(
            from == int.MinValue ? "Any score" : $"{from} or more", tops.Count(t => t >= floor), BackgroundTone.Pending));

        return new BarsPanelDto(
            "Why searches found no confident pick",
            $"Songs whose list has candidates but none to download, by the best candidate's score against the bar of {bar}. At or above the bar, a list failed another check: a still picture, the wrong artist, a length that does not fit, or a file over the size limit.",
            BackgroundFormat.Count,
            bars);
    }
}
