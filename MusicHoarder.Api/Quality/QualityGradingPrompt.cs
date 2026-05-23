using System.Text.Json;
using System.Text.Json.Serialization;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Quality;

public record GradingIssue(string Code, string Severity, string? Detail);

public record GradingResult(
    int Score,
    SongQualityVerdict Verdict,
    string? Summary,
    IReadOnlyList<GradingIssue> Issues);

/// <summary>
/// Builds the grading messages and parses the model's JSON reply. Versioned: bump
/// <see cref="Version"/> whenever the wording changes so stored grades stay comparable.
/// </summary>
public static class QualityGradingPrompt
{
    public const int Version = 1;

    private static readonly JsonSerializerOptions DossierJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const string System =
        """
        You are a meticulous music-library quality auditor. The pipeline you are auditing scans
        audio files, fingerprints them, queries several metadata providers (AcoustID, MusicBrainz,
        Deezer, Spotify, Apple Music, community trackers), then picks a "winning" set of tags and a
        destination path. Your job is to judge whether the result it chose for ONE song is correct
        and high quality — NOT to re-identify the song yourself.

        You will be given a JSON dossier: the file path + embedded tags as found on disk, every
        provider attempt and the candidate it returned, the field-level change log, the final chosen
        metadata, and the destination path the file would be written to.

        Grade how trustworthy the FINAL chosen metadata + destination are, given the evidence:

        - Excellent (90-100): correct and corroborated by ≥2 independent providers (or strong IDs
          like a matching ISRC/MBID), complete (artist, title, album, year), and the destination
          path matches the chosen metadata.
        - Good (70-89): correct and consistent, but thinly sourced or missing minor fields.
        - Questionable (40-69): plausible but unverified, internally inconsistent, or a single
          low-confidence source; a human should check.
        - Wrong (1-39): the chosen metadata or destination contradicts the source file's own tags
          or filename, OR no provider matched yet the song was still given a confident-looking
          identity (artist/title/album that look invented or borrowed from an unrelated track).
        - Ungradeable (0): genuinely no information to judge.

        Pay special attention to the failure this audit exists to catch: a file where every provider
        returned "no match" but the final metadata and destination path nonetheless name a specific,
        unrelated song/artist/album. That is "wrong" with an "unsupported_identity" issue.

        Reply with ONLY a JSON object, no prose, no code fences:
        {
          "score": <integer 0-100>,
          "verdict": "excellent" | "good" | "questionable" | "wrong" | "ungradeable",
          "summary": "<one sentence, plain English>",
          "issues": [ { "code": "<snake_case>", "severity": "low"|"medium"|"high", "detail": "<short>" } ]
        }

        Useful issue codes (use these where they apply, add others as needed):
        unsupported_identity, artist_changed, title_changed, album_changed, no_provider_match,
        low_confidence, single_source, duration_mismatch, path_metadata_mismatch,
        missing_year, missing_album, embedded_tags_overwritten, looks_correct.
        """;

    public static IReadOnlyList<ChatMessage> BuildMessages(SongGradingDossier dossier)
    {
        var json = JsonSerializer.Serialize(dossier, DossierJson);
        return
        [
            new ChatMessage("system", System),
            new ChatMessage("user", $"Grade this song's enrichment result.\n\nDOSSIER:\n{json}"),
        ];
    }

    /// <summary>Parses the model reply into a <see cref="GradingResult"/>, tolerating code fences and stray prose.</summary>
    public static GradingResult Parse(string content)
    {
        var json = ExtractJsonObject(content);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var score = root.TryGetProperty("score", out var s) && s.TryGetInt32(out var sc)
            ? Math.Clamp(sc, 0, 100)
            : 0;

        var verdictRaw = root.TryGetProperty("verdict", out var v) ? v.GetString() : null;
        var verdict = ParseVerdict(verdictRaw, score);

        string? summary = root.TryGetProperty("summary", out var sum) ? sum.GetString() : null;
        if (summary is { Length: > 1024 }) summary = summary[..1024];

        var issues = new List<GradingIssue>();
        if (root.TryGetProperty("issues", out var iss) && iss.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in iss.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var code = item.TryGetProperty("code", out var c) ? c.GetString() : null;
                if (string.IsNullOrWhiteSpace(code)) continue;
                var severity = item.TryGetProperty("severity", out var sev) ? sev.GetString() ?? "medium" : "medium";
                var detail = item.TryGetProperty("detail", out var d) ? d.GetString() : null;
                issues.Add(new GradingIssue(code!, severity, detail));
            }
        }

        return new GradingResult(score, verdict, summary, issues);
    }

    private static SongQualityVerdict ParseVerdict(string? raw, int score) => raw?.Trim().ToLowerInvariant() switch
    {
        "excellent" => SongQualityVerdict.Excellent,
        "good" => SongQualityVerdict.Good,
        "questionable" => SongQualityVerdict.Questionable,
        "wrong" => SongQualityVerdict.Wrong,
        "ungradeable" => SongQualityVerdict.Ungradeable,
        // Fall back to bucketing by score when the model omits/garbles the label.
        _ => score switch
        {
            >= 90 => SongQualityVerdict.Excellent,
            >= 70 => SongQualityVerdict.Good,
            >= 40 => SongQualityVerdict.Questionable,
            >= 1 => SongQualityVerdict.Wrong,
            _ => SongQualityVerdict.Ungradeable,
        },
    };

    private static string ExtractJsonObject(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start >= 0 && end > start)
            return content[start..(end + 1)];
        return content;
    }
}
