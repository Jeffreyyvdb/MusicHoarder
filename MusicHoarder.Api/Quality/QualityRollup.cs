using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Quality;

public record IssueCount(string Code, int Count);

public record VerdictBreakdown(int Excellent, int Good, int Questionable, int Wrong, int Ungradeable);

/// <summary>Aggregated quality picture for a set of graded songs (a directory or the whole library).</summary>
public record QualityAggregate(
    int Graded,
    double? AverageScore,
    VerdictBreakdown Verdicts,
    IReadOnlyList<IssueCount> TopIssues);

/// <summary>Pure aggregation over a set of grades. No EF, no IO — directly unit-testable.</summary>
public static class QualityRollup
{
    public record GradeRow(SongQualityVerdict Verdict, int Score, string? IssuesJson);

    public readonly record struct PreParsedGradeRow(
        SongQualityVerdict Verdict,
        int Score,
        IReadOnlyList<string> IssueCodes);

    public static QualityAggregate Aggregate(IEnumerable<GradeRow> grades, int topIssues = 10)
    {
        var rows = grades as IReadOnlyList<GradeRow> ?? grades.ToList();
        var preParsed = new PreParsedGradeRow[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            preParsed[i] = new PreParsedGradeRow(r.Verdict, r.Score, GradingIssueJson.ParseCodes(r.IssuesJson));
        }
        return AggregatePreParsed(preParsed, topIssues);
    }

    public static QualityAggregate AggregatePreParsed(IEnumerable<PreParsedGradeRow> grades, int topIssues = 10)
    {
        int total = 0, excellent = 0, good = 0, questionable = 0, wrong = 0, ungradeable = 0;
        int scoredCount = 0;
        long scoreSum = 0;
        var issueCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in grades)
        {
            total++;
            switch (row.Verdict)
            {
                case SongQualityVerdict.Excellent: excellent++; scoredCount++; scoreSum += row.Score; break;
                case SongQualityVerdict.Good: good++; scoredCount++; scoreSum += row.Score; break;
                case SongQualityVerdict.Questionable: questionable++; scoredCount++; scoreSum += row.Score; break;
                case SongQualityVerdict.Wrong: wrong++; scoredCount++; scoreSum += row.Score; break;
                case SongQualityVerdict.Ungradeable: ungradeable++; break;
                default: scoredCount++; scoreSum += row.Score; break;
            }

            foreach (var code in row.IssueCodes)
                issueCounts[code] = issueCounts.GetValueOrDefault(code) + 1;
        }

        // Average excludes Ungradeable rows — a score of 0 there means "no info", not "bad".
        double? avg = scoredCount > 0 ? Math.Round((double)scoreSum / scoredCount, 1) : null;
        var breakdown = new VerdictBreakdown(excellent, good, questionable, wrong, ungradeable);

        var top = issueCounts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(topIssues)
            .Select(kv => new IssueCount(kv.Key, kv.Value))
            .ToList();

        return new QualityAggregate(total, avg, breakdown, top);
    }

}
