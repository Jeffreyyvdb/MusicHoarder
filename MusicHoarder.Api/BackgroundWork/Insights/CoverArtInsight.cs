using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Artwork;
using MusicHoarder.Api.BackgroundWork.Charts;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.BackgroundWork.Insights;

public sealed class CoverArtInsight(
    MusicHoarderDbContext db,
    IOptions<MusicEnricherOptions> options,
    BackgroundWorkStatus status,
    TimeProvider time) : IBackgroundJobInsight
{
    private const int DailyBuckets = 30;

    public string Id => BackgroundJobIds.CoverArt;

    private enum Folder
    {
        HasCover,
        UpgradeWaiting,
        UpgradeNotFound,
        UpgradeFailed,
        MissingUntried,
        MissingNotFound,
        MissingFailed,
    }

    private sealed record Data(
        DateTime Now,
        Dictionary<Folder, int> Folders,
        int DueNow,
        List<AlbumCoverFetchAttempt> Attempts,
        List<DateTime> WrittenAt)
    {
        public int Total => Folders.Values.Sum();
        public int Covered => Folders.Where(f => f.Key <= Folder.UpgradeFailed).Sum(f => f.Value);
    }

    private async Task<Data> LoadAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var opts = options.Value;

        var rows = await db.Songs.AsNoTracking()
            .WhereBuiltForCoverSweep()
            .Select(s => new { s.DestinationPath, s.HasCoverArt })
            .ToListAsync(ct);
        var covered = rows
            .GroupBy(r => Path.GetDirectoryName(r.DestinationPath!) ?? string.Empty, StringComparer.Ordinal)
            .Where(g => g.Key.Length > 0)
            .ToDictionary(g => g.Key, g => g.Any(r => r.HasCoverArt), StringComparer.Ordinal);

        var attempts = await db.AlbumCoverFetchAttempts.AsNoTracking().ToListAsync(ct);
        var attemptOf = attempts.ToDictionary(a => a.AlbumFolder, StringComparer.Ordinal);

        var writes = await db.LibraryWriteEvents.AsNoTracking()
            .Where(e => e.Kind == LibraryWriteEventKind.AlbumCoverWritten && e.AlbumFolder != null)
            .Select(e => new CoverWriteRecord(e.Id, e.AlbumFolder!, e.WrittenAtUtc, e.NewValue))
            .ToListAsync(ct);
        var caaFolders = opts.EnableSpotifyCovers ? CoverArtSweepRules.CoverArtArchiveFolders(writes) : [];

        var counts = Enum.GetValues<Folder>().ToDictionary(f => f, _ => 0);
        var due = 0;
        var considered = new List<AlbumCoverFetchAttempt>();
        foreach (var (folder, hasCover) in covered)
        {
            attemptOf.TryGetValue(folder, out var attempt);
            Folder kind;
            if (hasCover)
            {
                if (!caaFolders.Contains(folder))
                    kind = Folder.HasCover;
                else if (CoverArtSweepRules.IsUpgradeDue(attempt, now))
                {
                    kind = Folder.UpgradeWaiting;
                    due++;
                }
                else
                {
                    kind = attempt!.Status == AlbumCoverFetchStatus.UpgradeFailed ? Folder.UpgradeFailed : Folder.UpgradeNotFound;
                }
            }
            else
            {
                if (CoverArtSweepRules.IsMissingFolderDue(attempt, now))
                    due++;
                kind = attempt?.Status switch
                {
                    AlbumCoverFetchStatus.Failed => Folder.MissingFailed,
                    AlbumCoverFetchStatus.NotFound => Folder.MissingNotFound,
                    _ => Folder.MissingUntried,
                };
            }
            counts[kind]++;
            if (kind != Folder.HasCover && attempt is not null)
                considered.Add(attempt);
        }

        return new Data(now, counts, due, considered, [.. writes.Select(w => w.WrittenAtUtc)]);
    }

    public async Task<BackgroundJobSummaryDto> SummarizeAsync(CancellationToken ct)
        => Summarize(await LoadAsync(ct));

    private BackgroundJobSummaryDto Summarize(Data d)
        => BackgroundJobSummaries.Build(
            BackgroundJobDefinitions.CoverArt,
            options.Value.EnableExternalCoverArtFetch ? null : "Off: external cover art fetching is switched off.",
            status.Get(Id),
            d.WrittenAt.Count == 0 ? null : d.WrittenAt.Max(),
            new BackgroundHeadlineDto(
                d.Covered, d.Total, BackgroundFormat.Count,
                $"of {BackgroundText.Count(d.Total)} album folders have a cover", null, null),
            TimeBuckets.DailyCounts(d.Now, TimeBuckets.TrendDays, d.WrittenAt),
            "covers written per day");

    public async Task<BackgroundJobDetailDto> DescribeAsync(CancellationToken ct)
    {
        var d = await LoadAsync(ct);
        var opts = options.Value;
        var f = d.Folders;

        var samples = d.WrittenAt.Select(t => (t, 0, 1.0))
            .Concat(d.Attempts.Select(a => (a.LastAttemptAtUtc, a.Status switch
            {
                AlbumCoverFetchStatus.NotFound => 1,
                AlbumCoverFetchStatus.Failed => 2,
                AlbumCoverFetchStatus.UpgradeNotFound => 3,
                _ => 4,
            }, 1.0)));

        var nextRetry = d.Attempts
            .Where(a => a.NextRetryAfterUtc > d.Now)
            .Select(a => a.NextRetryAfterUtc)
            .Min();
        var runtime = status.Get(Id);

        var panels = new List<BackgroundPanelDto>
        {
            BackgroundPanels.Breakdown(
                "Album folders by cover", "One album folder is one square in the grid.", "folders",
            [
                new(nameof(Folder.HasCover), "Has a cover", f[Folder.HasCover], BackgroundTone.Good),
                new(nameof(Folder.UpgradeWaiting), "Waiting for a Spotify upgrade", f[Folder.UpgradeWaiting], BackgroundTone.Pending,
                    "A Cover Art Archive cover the sweep will try to replace with Spotify's."),
                new(nameof(Folder.UpgradeNotFound), "No better cover on Spotify", f[Folder.UpgradeNotFound], BackgroundTone.Muted,
                    "Spotify had no verified match; asked again after the cooldown."),
                new(nameof(Folder.UpgradeFailed), "Upgrade failed, retrying", f[Folder.UpgradeFailed], BackgroundTone.Warning),
                new(nameof(Folder.MissingUntried), "Missing, not tried yet", f[Folder.MissingUntried], BackgroundTone.Pending),
                new(nameof(Folder.MissingNotFound), "Missing, none found", f[Folder.MissingNotFound], BackgroundTone.Neutral,
                    "Every provider answered and none had one; asked again after the cooldown."),
                new(nameof(Folder.MissingFailed), "Missing, a provider failed", f[Folder.MissingFailed], BackgroundTone.Bad,
                    "A provider errored or rate-limited, so it is retried sooner."),
            ]),
            new SeriesPanelDto(
                "What the sweep did each day, last 30 days",
                "A folder keeps only its last failed attempt, so failures count on the day of the latest try; a cover that was written has no row left and counts from the write log.",
                "day", BackgroundFormat.Count, true,
                [
                    new("written", "Cover written", BackgroundTone.Good),
                    new("not-found", "None found", BackgroundTone.Neutral),
                    new("failed", "Provider failed", BackgroundTone.Bad),
                    new("upgrade-not-found", "No better cover", BackgroundTone.Muted),
                    new("upgrade-failed", "Upgrade failed", BackgroundTone.Warning),
                ],
                TimeBuckets.Fill(d.Now, hourly: false, DailyBuckets, 5, samples), []),
            new FiguresPanelDto("The sweep", null,
            [
                BackgroundPanels.Moment("Next sweep", runtime?.NextRunAtUtc, runtime is null ? "Nothing reported since the API started." : null),
                BackgroundPanels.Moment("Soonest retry", nextRetry, nextRetry is null ? "No folder is cooling down." : null),
                BackgroundPanels.Count("Folders due now", d.DueNow, "never tried, or their cooldown has passed"),
                BackgroundPanels.Count(
                    "Folders per sweep", opts.ExternalCoverArtSweepBatchSize,
                    $"every {BackgroundText.Duration(opts.ExternalCoverArtSweepIntervalMinutes * 60.0)}"),
            ]),
        };

        return new BackgroundJobDetailDto(Summarize(d), panels);
    }
}
