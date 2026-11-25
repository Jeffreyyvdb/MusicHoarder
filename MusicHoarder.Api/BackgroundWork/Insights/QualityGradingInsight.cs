using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.BackgroundWork.Charts;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Quality;
using MusicHoarder.Api.Settings;

namespace MusicHoarder.Api.BackgroundWork.Insights;

public sealed class QualityGradingInsight(
    MusicHoarderDbContext db,
    IOptionsMonitor<QualityGradingOptions> options,
    IRuntimeSettingsService runtimeSettings,
    BackgroundWorkStatus status,
    TimeProvider time) : IBackgroundJobInsight
{
    private const int DailyBuckets = 30;
    private const int RateWindowDays = 7;

    private static readonly (SongQualityVerdict Verdict, string Label, string Tone)[] Verdicts =
    [
        (SongQualityVerdict.Excellent, "Excellent", BackgroundTone.Good),
        (SongQualityVerdict.Good, "Good", BackgroundTone.Good),
        (SongQualityVerdict.Questionable, "Questionable", BackgroundTone.Warning),
        (SongQualityVerdict.Wrong, "Wrong", BackgroundTone.Bad),
        (SongQualityVerdict.Ungradeable, "Could not be judged", BackgroundTone.Muted),
    ];

    public string Id => BackgroundJobIds.QualityGrading;

    private sealed record Data(
        DateTime Now,
        int Gradeable,
        int Waiting,
        Dictionary<SongQualityVerdict, int> Current,
        int[] ScoreHistogram,
        int Outdated,
        List<(DateTime At, SongQualityVerdict Verdict)> Recent,
        double RatePerDay,
        string? OffReason);

    private async Task<Data> LoadAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var opts = options.CurrentValue;

        var songs = await db.Songs.AsNoTracking().WhereGradeable()
            .Select(s => new { s.Id, s.EnrichedAtUtc })
            .ToListAsync(ct);
        var gradeable = songs.Select(s => s.Id).ToHashSet();

        var latest = (await db.SongQualityGrades.AsNoTracking().LatestPerSong()
                .Select(g => new
                {
                    g.Id, g.SongId, g.GradedAtUtc, g.Score, g.Verdict, g.PromptVersion, g.Model,
                    MaybeBlank = g.Verdict == SongQualityVerdict.Ungradeable && (g.Summary == null || g.Summary == ""),
                })
                .ToListAsync(ct))
            .Where(g => gradeable.Contains(g.SongId))
            .GroupBy(g => g.SongId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.GradedAtUtc).ThenByDescending(x => x.Id).First());

        var maybeBlankIds = latest.Values.Where(g => g.MaybeBlank).Select(g => g.Id).ToList();
        var blank = maybeBlankIds.Count == 0
            ? []
            : (await db.SongQualityGrades.AsNoTracking()
                    .Where(g => maybeBlankIds.Contains(g.Id))
                    .Select(g => new { g.Id, g.Verdict, g.Summary, g.RawResponseJson })
                    .ToListAsync(ct))
                .Where(g => BlankGrade.IsBlank(g.Verdict, g.Summary, g.RawResponseJson))
                .Select(g => g.Id)
                .ToHashSet();

        var current = Verdicts.ToDictionary(v => v.Verdict, _ => 0);
        var histogram = new int[10];
        var waiting = 0;
        var outdated = 0;
        foreach (var song in songs)
        {
            latest.TryGetValue(song.Id, out var grade);
            if (GradeFreshness.NeedsGrading(grade?.GradedAtUtc, song.EnrichedAtUtc, grade is not null && blank.Contains(grade.Id)))
            {
                waiting++;
                continue;
            }

            current[grade!.Verdict]++;
            if (grade.Verdict != SongQualityVerdict.Ungradeable)
                histogram[Math.Min(9, Math.Max(0, grade.Score) / 10)]++;
            if (GradeFreshness.IsSongGradeOutdated(grade.PromptVersion, grade.Model, opts.Model))
                outdated++;
        }

        var monthAgo = now.AddDays(-DailyBuckets);
        var recent = (await db.SongQualityGrades.AsNoTracking()
                .Where(g => g.GradedAtUtc > monthAgo)
                .Select(g => new { g.GradedAtUtc, g.Verdict })
                .ToListAsync(ct))
            .Select(g => (g.GradedAtUtc, g.Verdict))
            .ToList();
        var oldest = await db.SongQualityGrades.AsNoTracking().MinAsync(g => (DateTime?)g.GradedAtUtc, ct);
        var rate = EtaEstimator.RatePerDay(
            recent.Count(g => g.GradedAtUtc > now.AddDays(-RateWindowDays)), oldest, now, RateWindowDays);

        var enabled = (await runtimeSettings.GetAsync(ct)).QualityGradingEnabled;
        var offReason = !opts.IsConfigured
            ? "Off: no grading model is configured."
            : !enabled
                ? "Off: quality grading is switched off in Settings."
                : !opts.AutoGradeAfterEnrichment
                    ? "Off: automatic grading is switched off; songs are graded by hand."
                    : null;

        return new Data(now, songs.Count, waiting, current, histogram, outdated, recent, rate, offReason);
    }

    public async Task<BackgroundJobSummaryDto> SummarizeAsync(CancellationToken ct)
        => Summarize(await LoadAsync(ct));

    private BackgroundJobSummaryDto Summarize(Data d)
    {
        var graded = d.Gradeable - d.Waiting;
        return BackgroundJobSummaries.Build(
            BackgroundJobDefinitions.QualityGrading,
            d.OffReason,
            status.Get(Id),
            d.Recent.Count == 0 ? null : d.Recent.Max(g => g.At),
            new BackgroundHeadlineDto(
                graded, d.Gradeable, BackgroundFormat.Count,
                $"of {BackgroundText.Count(d.Gradeable)} enriched songs have a current grade",
                EtaEstimator.Eta(d.Waiting, d.RatePerDay, d.Now),
                d.RatePerDay > 0
                    ? $"at the last {RateWindowDays} days' rate of {BackgroundText.Count(d.RatePerDay)} grades a day"
                    : null),
            TimeBuckets.DailyCounts(d.Now, TimeBuckets.TrendDays, d.Recent.Select(g => g.At)),
            "songs graded per day");
    }

    public async Task<BackgroundJobDetailDto> DescribeAsync(CancellationToken ct)
    {
        var d = await LoadAsync(ct);

        var segments = new List<BackgroundPanels.Segment>(
            Verdicts.Select(v => new BackgroundPanels.Segment(v.Verdict.ToString().ToLowerInvariant(), v.Label, d.Current[v.Verdict], v.Tone)))
        {
            new("waiting", "Waiting to be graded", d.Waiting, BackgroundTone.Pending,
                "Never graded, enriched again since its grade, or its grade was an empty reply."),
        };

        var histogram = d.ScoreHistogram.Select((n, i) => new BarDto(
            i == 9 ? "90 to 100" : $"{i * 10} to {i * 10 + 9}", n,
            i < 4 ? BackgroundTone.Bad : i < 7 ? BackgroundTone.Warning : BackgroundTone.Good)).ToList();

        var gradeableAlbums = db.CanonicalAlbums.AsNoTracking().WhereGradeable();
        var albumsGraded = await gradeableAlbums
            .CountAsync(a => db.CanonicalAlbumQualityGrades.Any(g => g.CanonicalAlbumId == a.Id), ct);
        var albums = await gradeableAlbums.CountAsync(ct);
        var weekAgo = d.Now.AddDays(-RateWindowDays);
        var shadowRuns = await db.GraderComparisonRuns.AsNoTracking()
            .CountAsync(r => r.Arm == GraderArm.Decision && r.CreatedAtUtc > weekAgo, ct);

        var panels = new List<BackgroundPanelDto>
        {
            BackgroundPanels.Breakdown(
                "Latest verdict of every enriched song", null, "songs", segments),
            new BarsPanelDto(
                "Scores of the current grades", "Songs the grader could not judge are not scored and are left out.",
                BackgroundFormat.Count, histogram),
            new SeriesPanelDto(
                "Grades per day by verdict, last 30 days",
                "Every grade written, including re-grades of a song.",
                "day", BackgroundFormat.Count, true,
                [.. Verdicts.Select(v => new SeriesDefinitionDto(v.Verdict.ToString().ToLowerInvariant(), v.Label, v.Tone))],
                TimeBuckets.Fill(
                    d.Now, hourly: false, DailyBuckets, Verdicts.Length,
                    d.Recent.Select(g => (g.At, Array.FindIndex(Verdicts, v => v.Verdict == g.Verdict), 1.0))),
                []),
            new FiguresPanelDto("Around the grader", null,
            [
                BackgroundPanels.Count("Albums graded", albumsGraded, $"of {BackgroundText.Count(albums)} albums matched to a catalog"),
                BackgroundPanels.Count(
                    "Grades from an older prompt or model", d.Outdated,
                    "kept as they are; regraded only when you ask", d.Outdated > 0 ? BackgroundTone.Warning : BackgroundTone.Neutral),
                BackgroundPanels.Count("Decision-model shadow grades, last 7 days", shadowRuns, "the grader trial, which never changes a grade"),
            ]),
        };

        return new BackgroundJobDetailDto(Summarize(d), panels);
    }
}
