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
        try { return JsonSerializer.Deserialize<List<GradingIssue>>(json, Json) ?? []; }
        catch { return []; }
    }

    /// <summary>The non-blank issue codes only, for frequency rollups.</summary>
    public static IEnumerable<string> ParseCodes(string? json) =>
        Parse(json).Where(i => !string.IsNullOrWhiteSpace(i.Code)).Select(i => i.Code);
}
