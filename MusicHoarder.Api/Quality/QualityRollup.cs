using System.Text.Json;
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

    public static QualityAggregate Aggregate(IEnumerable<GradeRow> grades, int topIssues = 10)
    {
        var rows = grades as IReadOnlyList<GradeRow> ?? grades.ToList();

        var breakdown = new VerdictBreakdown(
            rows.Count(r => r.Verdict == SongQualityVerdict.Excellent),
            rows.Count(r => r.Verdict == SongQualityVerdict.Good),
            rows.Count(r => r.Verdict == SongQualityVerdict.Questionable),
            rows.Count(r => r.Verdict == SongQualityVerdict.Wrong),
            rows.Count(r => r.Verdict == SongQualityVerdict.Ungradeable));

        // Average excludes Ungradeable rows — a score of 0 there means "no info", not "bad".
        var scored = rows.Where(r => r.Verdict != SongQualityVerdict.Ungradeable).ToList();
        double? avg = scored.Count > 0 ? Math.Round(scored.Average(r => r.Score), 1) : null;

        var issueCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            foreach (var code in ParseIssueCodes(row.IssuesJson))
                issueCounts[code] = issueCounts.GetValueOrDefault(code) + 1;
        }

        var top = issueCounts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(topIssues)
            .Select(kv => new IssueCount(kv.Key, kv.Value))
            .ToList();

        return new QualityAggregate(rows.Count, avg, breakdown, top);
    }

    private static IEnumerable<string> ParseIssueCodes(string? issuesJson)
    {
        if (string.IsNullOrWhiteSpace(issuesJson)) yield break;
        List<GradingIssue>? issues = null;
        try { issues = JsonSerializer.Deserialize<List<GradingIssue>>(issuesJson, JsonOptions); }
        catch { yield break; }
        if (issues is null) yield break;
        foreach (var issue in issues)
            if (!string.IsNullOrWhiteSpace(issue.Code))
                yield return issue.Code;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
