using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Assets;
using MusicHoarder.Api.BackgroundWork.Charts;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Scanner;

namespace MusicHoarder.Api.BackgroundWork.Insights;

/// <summary>
/// The adoption job and the stored files it registers. Songs are the admin's (the ambient filter);
/// assets have no owner, so their counts are instance-wide.
/// </summary>
public sealed class AudioAssetsInsight(
    MusicHoarderDbContext db,
    IOptions<MusicEnricherOptions> options,
    BackgroundWorkStatus status,
    TimeProvider time) : IBackgroundJobInsight
{
    public string Id => BackgroundJobIds.AudioAssets;

    private sealed record Data(DateTime Now, int Waiting, DateTime? LastCreated, List<(DateTime Day, int Count, DateTime Last)> AssetsPerDay);

    private async Task<Data> LoadAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var waiting = await db.Songs.AsNoTracking().WhereAdoptable(options.Value, db.UpgradeRequests).CountAsync(ct);
        var since = TimeBuckets.Starts(now, hourly: false, TimeBuckets.TrendDays)[0];
        var perDay = (await db.AudioAssets.AsNoTracking()
                .Where(a => a.CreatedAtUtc >= since)
                .GroupBy(a => a.CreatedAtUtc.Date)
                .Select(g => new { Day = g.Key, Count = g.Count(), Last = g.Max(a => a.CreatedAtUtc) })
                .ToListAsync(ct))
            .Select(g => (g.Day, g.Count, g.Last))
            .ToList();
        var lastCreated = await db.AudioAssets.AsNoTracking().MaxAsync(a => (DateTime?)a.CreatedAtUtc, ct);
        return new Data(now, waiting, lastCreated, perDay);
    }

    public async Task<BackgroundJobSummaryDto> SummarizeAsync(CancellationToken ct)
        => Summarize(await LoadAsync(ct));

    private BackgroundJobSummaryDto Summarize(Data d)
        => BackgroundJobSummaries.Build(
            BackgroundJobDefinitions.AudioAssets,
            options.Value.EnableAudioAssetAdoption ? null : "Off: MusicEnricher:EnableAudioAssetAdoption is not set.",
            status.Get(Id),
            d.LastCreated,
            new BackgroundHeadlineDto(d.Waiting, null, BackgroundFormat.Count, "songs have a file not yet registered as a stored asset", null, null),
            TimeBuckets.DailyTotals(d.Now, TimeBuckets.TrendDays, d.AssetsPerDay.Select(a => (a.Day, (double)a.Count))),
            "stored files registered per day");

    public async Task<BackgroundJobDetailDto> DescribeAsync(CancellationToken ct)
    {
        var d = await LoadAsync(ct);
        var songs = db.Songs.AsNoTracking().Where(s => s.DeletedAtUtc == null && !s.IsSynthetic).ExcludingDemoTenant();
        var linked = await songs.CountAsync(s => s.AudioAssetId != null, ct);
        var total = await songs.CountAsync(ct);

        var assets = db.AudioAssets.AsNoTracking();
        var assetCount = await assets.CountAsync(a => a.PurgedAtUtc == null, ct);
        var bytes = await assets.Where(a => a.PurgedAtUtc == null).SumAsync(a => (long?)a.SizeBytes, ct) ?? 0;
        var purged = await assets.CountAsync(a => a.PurgedAtUtc != null, ct);
        var store = options.Value.ResolvedContentStoreDirectory;
        var stored = string.IsNullOrWhiteSpace(store)
            ? 0
            : await assets.CountAsync(a => a.PurgedAtUtc == null && a.StoragePath.StartsWith(IndexService.NormalizeRootPrefix(store)), ct);

        var panels = new List<BackgroundPanelDto>
        {
            BackgroundPanels.Breakdown("Songs by stored file", null, "songs",
            [
                new BackgroundPanels.Segment("linked", "Linked to a stored file", linked, BackgroundTone.Good),
                new BackgroundPanels.Segment("waiting", "Waiting to be read", d.Waiting, BackgroundTone.Pending),
                new BackgroundPanels.Segment(
                    "other", "Not adopted", Math.Max(0, total - linked - d.Waiting), BackgroundTone.Muted,
                    "Released downloads and files outside the source and download folders"),
            ]),
            new FiguresPanelDto("Stored files", "Assets have no owner: these count every library on the instance.",
            [
                BackgroundPanels.Count("Stored files", assetCount),
                BackgroundPanels.Bytes("Their size", bytes),
                BackgroundPanels.Count("In the content store", stored, "the rest are registered where they already were"),
                BackgroundPanels.Count("Purged", purged, "deleted on purpose; the rows stay"),
            ]),
        };
        return new BackgroundJobDetailDto(Summarize(d), panels);
    }
}
