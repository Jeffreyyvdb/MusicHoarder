using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.BackgroundWork.Charts;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.BackgroundWork.Insights;

/// <summary>
/// Music videos, from <see cref="SongMusicVideo"/> alone. The candidate prefetch behind them is being
/// rewritten, so this reports what exists without reaching into it; its state stays unknown until the
/// service reports.
/// </summary>
public sealed class MusicVideosInsight(
    MusicHoarderDbContext db,
    BackgroundWorkStatus status,
    TimeProvider time) : IBackgroundJobInsight
{
    public string Id => BackgroundJobIds.MusicVideos;

    private sealed record Data(DateTime Now, int Ready, int Fetching, int Failed, List<DateTime> FetchedAt);

    private async Task<Data> LoadAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var counts = await db.SongMusicVideos.AsNoTracking()
            .GroupBy(v => v.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        int Of(MusicVideoStatus s) => counts.Where(c => c.Status == s).Sum(c => c.Count);

        var since = now.AddDays(-TimeBuckets.TrendDays);
        var fetchedAt = await db.SongMusicVideos.AsNoTracking()
            .Where(v => v.Status == MusicVideoStatus.Ready && v.FetchedAtUtc > since)
            .Select(v => v.FetchedAtUtc)
            .ToListAsync(ct);
        return new Data(now, Of(MusicVideoStatus.Ready), Of(MusicVideoStatus.Fetching), Of(MusicVideoStatus.Failed), fetchedAt);
    }

    public async Task<BackgroundJobSummaryDto> SummarizeAsync(CancellationToken ct)
        => Summarize(await LoadAsync(ct));

    private BackgroundJobSummaryDto Summarize(Data d)
        => BackgroundJobSummaries.Build(
            BackgroundJobDefinitions.MusicVideos,
            null,
            status.Get(Id),
            d.FetchedAt.Count == 0 ? null : d.FetchedAt.Max(),
            new BackgroundHeadlineDto(d.Ready, null, BackgroundFormat.Count, "music videos are ready", null, null),
            TimeBuckets.DailyCounts(d.Now, TimeBuckets.TrendDays, d.FetchedAt),
            "music videos fetched per day");

    public async Task<BackgroundJobDetailDto> DescribeAsync(CancellationToken ct)
    {
        var d = await LoadAsync(ct);
        var panels = new List<BackgroundPanelDto>
        {
            BackgroundPanels.Breakdown("Music videos by state", null, "videos",
            [
                new(nameof(MusicVideoStatus.Ready), "Ready", d.Ready, BackgroundTone.Good),
                new(nameof(MusicVideoStatus.Fetching), "Downloading", d.Fetching, BackgroundTone.Pending),
                new(nameof(MusicVideoStatus.Failed), "Failed", d.Failed, BackgroundTone.Bad),
            ]),
        };
        return new BackgroundJobDetailDto(Summarize(d), panels);
    }
}
