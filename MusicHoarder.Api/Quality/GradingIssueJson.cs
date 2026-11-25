using System.Text.Json;

namespace MusicHoarder.Api.Quality;

/// <summary>
/// Decodes the grading-issue JSON persisted on <see cref="Persistence.SongQualityGrade.IssuesJson"/>
/// and <see cref="Persistence.CanonicalAlbumQualityGrade.IssuesJson"/> (camelCase, written by the
/// grading services from the model's response). Empty or malformed payloads read as "no issues" — a
/// stored grade must never break a read surface.
/// </summary>
public static class GradingIssueJson
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static List<GradingIssue> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        var trimmed = json.AsSpan().Trim();
        if (trimmed.Length <= 2 && trimmed.SequenceEqual("[]")) return [];
        try { return JsonSerializer.Deserialize<List<GradingIssue>>(json, Json) ?? []; }
        catch { return []; }
    }

    /// <summary>The non-blank issue codes only, for frequency rollups.</summary>
    public static List<string> ParseCodes(string? json)
    {
        var parsed = Parse(json);
        if (parsed.Count == 0) return [];
        var codes = new List<string>(parsed.Count);
        foreach (var issue in parsed)
        {
            if (!string.IsNullOrWhiteSpace(issue.Code))
                codes.Add(issue.Code);
        }
        return codes;
    }
}
