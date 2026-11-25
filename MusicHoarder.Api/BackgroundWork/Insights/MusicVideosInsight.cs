using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.BackgroundWork.Charts;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Storage;

namespace MusicHoarder.Api.BackgroundWork.Insights;

/// <summary>
/// The music-video candidate prefetch. The songs it considers are the prefetch's own window
/// (<see cref="MusicVideoPrefetchQueries"/>), and whether a song is waiting for a search is the cache's own
/// <see cref="MusicVideoCandidateCache.NeedsSearch"/>.
/// </summary>
public sealed class MusicVideosInsight(
    MusicHoarderDbContext db,
    MusicVideoCandidateCache cache,
    IOptionsMonitor<MusicEnricherOptions> options,
    StorageUsageSnapshotStore storage,
    BackgroundWorkStatus status,
    TimeProvider time) : IBackgroundJobInsight
{
    private const int DailyBuckets = 30;

    /// <summary>Lower edge of each score bucket of the histogram, and the tone it is drawn in.</summary>
    private static readonly (int From, string Label, string Tone)[] ScoreBuckets =
    [
        (int.MinValue, "Below 0", BackgroundTone.Bad),
        (0, "0 to 19", BackgroundTone.Warning),
        (20, "20 to 39", BackgroundTone.Neutral),
        (40, "40 to 59", BackgroundTone.Neutral),
        (60, "60 to 79", BackgroundTone.Good),
        (80, "80 and up", BackgroundTone.Good),
    ];

    public string Id => BackgroundJobIds.MusicVideos;

    private enum Slot { HasVideo, Moving, LowMotion, SearchFailed, Waiting }

    private sealed record Data(
        DateTime Now,
        bool VideosEverFetched,
        Dictionary<Slot, int> Window,
        List<int> TopScores,
        int Ready,
        int Failed,
        List<DateTime> FetchedAt,
        List<DateTime> SearchedAt);

    private async Task<Data> LoadAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var since = now.AddDays(-options.CurrentValue.MusicVideoCandidatePrefetchDays);
        var monthAgo = now.AddDays(-DailyBuckets);

        var videosEverFetched = await db.SongMusicVideos.IgnoreQueryFilters().AnyAsync(ct);

        var window = db.Songs.AsNoTracking().WhereInPrefetchWindow(since, db.AdminUserIds());
        var songs = await window
            .Select(s => new { s.Id, s.Artist, s.Title, s.DurationMs, s.DurationSeconds })
            .ToListAsync(ct);
        var withoutVideo = (await window
                .WhereWithoutUsableVideo(db.SongMusicVideos.IgnoreQueryFilters())
                .Select(s => s.Id)
                .ToListAsync(ct))
            .ToHashSet();

        var ids = withoutVideo.ToList();
        var sets = await db.SongVideoCandidateSets.AsNoTracking()
            .Where(c => ids.Contains(c.SongId))
            .ToDictionaryAsync(c => c.SongId, ct);

        var counts = Enum.GetValues<Slot>().ToDictionary(x => x, _ => 0);
        var topScores = new List<int>();
        foreach (var song in songs)
        {
            if (!withoutVideo.Contains(song.Id))
            {
                counts[Slot.HasVideo]++;
                continue;
            }

            if (!sets.TryGetValue(song.Id, out var set)
                || cache.NeedsSearch(set, MusicVideoCandidateCache.QueryKeyFor(song.Artist, song.Title, song.DurationMs, song.DurationSeconds), now))
            {
                counts[Slot.Waiting]++;
                continue;
            }

            if (set.Error is not null)
            {
                counts[Slot.SearchFailed]++;
                continue;
            }

            IReadOnlyList<CachedVideoCandidate> candidates;
            try
            {
                candidates = MusicVideoCandidateCache.ParseCandidates(set.CandidatesJson);
            }
            catch (JsonException)
            {
                counts[Slot.Waiting]++;
                continue;
            }

            counts[candidates.Any(c => c.Motion == MusicVideoMotion.RealVideo) ? Slot.Moving : Slot.LowMotion]++;
            if (candidates.Count > 0)
                topScores.Add(candidates[0].Score);
        }

        var ready = await db.SongMusicVideos.AsNoTracking().CountAsync(v => v.Status == MusicVideoStatus.Ready, ct);
        var failed = await db.SongMusicVideos.AsNoTracking().CountAsync(v => v.Status == MusicVideoStatus.Failed, ct);
        var fetchedAt = await db.SongMusicVideos.AsNoTracking()
            .Where(v => v.Status == MusicVideoStatus.Ready && v.FetchedAtUtc > monthAgo)
            .Select(v => v.FetchedAtUtc)
            .ToListAsync(ct);
        var searchedAt = await db.SongVideoCandidateSets.AsNoTracking()
            .Where(c => c.SearchedAtUtc > monthAgo)
            .Select(c => c.SearchedAtUtc)
            .ToListAsync(ct);

        return new Data(now, videosEverFetched, counts, topScores, ready, failed, fetchedAt, searchedAt);
    }

    public async Task<BackgroundJobSummaryDto> SummarizeAsync(CancellationToken ct)
        => Summarize(await LoadAsync(ct));

    private BackgroundJobSummaryDto Summarize(Data d)
    {
        var off = options.CurrentValue.MusicVideoCandidatePrefetchIntervalMinutes <= 0
            ? "Off: the candidate prefetch is switched off."
            : !d.VideosEverFetched
                ? "Off until a first music video has been fetched: the prefetch only runs where they are used."
                : null;

        var all = d.FetchedAt.Concat(d.SearchedAt).ToList();
        return BackgroundJobSummaries.Build(
            BackgroundJobDefinitions.MusicVideos,
            off,
            status.Get(Id),
            all.Count == 0 ? null : all.Max(),
            new BackgroundHeadlineDto(d.Ready, null, BackgroundFormat.Count, "music videos are ready", null, null),
            TimeBuckets.DailyCounts(d.Now, TimeBuckets.TrendDays, d.FetchedAt),
            "music videos fetched per day");
    }

    public async Task<BackgroundJobDetailDto> DescribeAsync(CancellationToken ct)
    {
        var d = await LoadAsync(ct);
        var w = d.Window;
        var days = options.CurrentValue.MusicVideoCandidatePrefetchDays;

        var buckets = new double[ScoreBuckets.Length];
        foreach (var score in d.TopScores)
        {
            var i = ScoreBuckets.Length - 1;
            while (i > 0 && score < ScoreBuckets[i].From)
                i--;
            buckets[i]++;
        }

        var samples = d.SearchedAt.Select(t => (t, 0, 1.0)).Concat(d.FetchedAt.Select(t => (t, 1, 1.0)));
        var snapshot = storage.Get().Snapshot;
        var videoFiles = snapshot?.Categories.FirstOrDefault(c => c.Key == CategoryKeys.Videos);

        var panels = new List<BackgroundPanelDto>
        {
            BackgroundPanels.Breakdown(
                $"Songs added in the last {days} days", "The songs the prefetch looks at: an admin's, enriched, added recently.", "songs",
            [
                new(nameof(Slot.HasVideo), "Has a music video", w[Slot.HasVideo], BackgroundTone.Good),
                new(nameof(Slot.Moving), "A real music video among the candidates", w[Slot.Moving], BackgroundTone.Neutral,
                    "The picker opens on a measured list."),
                new(nameof(Slot.LowMotion), "Only still or low-motion candidates", w[Slot.LowMotion], BackgroundTone.Warning,
                    "Cover images, lyric cards and slideshows; or candidates not measured yet."),
                new(nameof(Slot.SearchFailed), "Search failed", w[Slot.SearchFailed], BackgroundTone.Bad,
                    "YouTube refused or found nothing; tried again after a day."),
                new(nameof(Slot.Waiting), "Waiting for a search", w[Slot.Waiting], BackgroundTone.Pending),
            ]),
            new BarsPanelDto(
                "How well the best candidate matches", "The ranking score of each list's first candidate; higher is a closer match.",
                BackgroundFormat.Count,
                [.. ScoreBuckets.Select((b, i) => new BarDto(b.Label, buckets[i], b.Tone))]),
            new SeriesPanelDto(
                "Searches and downloads per day, last 30 days",
                "A song keeps one search record, so a search counts on the day of its latest search.",
                "day", BackgroundFormat.Count, false,
                [
                    new("searches", "Candidate searches", BackgroundTone.Pending),
                    new("fetched", "Videos fetched", BackgroundTone.Good),
                ],
                TimeBuckets.Fill(d.Now, hourly: false, DailyBuckets, 2, samples), []),
            new FiguresPanelDto("Videos",
                null,
            [
                BackgroundPanels.Count("Ready", d.Ready),
                BackgroundPanels.Count("Failed", d.Failed, "downloads that did not finish", d.Failed > 0 ? BackgroundTone.Bad : BackgroundTone.Neutral),
                snapshot is not null && videoFiles is not null
                    ? new FigureDto("Disk used by videos", videoFiles.Bytes, BackgroundFormat.Bytes, snapshot.ComputedAtUtc,
                        $"{BackgroundText.Count(videoFiles.Files)} files, measured at the time shown", BackgroundTone.Neutral)
                    : BackgroundPanels.Moment("Disk used by videos", null, "The storage measurement has not run yet."),
            ]),
        };

        return new BackgroundJobDetailDto(Summarize(d), panels);
    }
}
