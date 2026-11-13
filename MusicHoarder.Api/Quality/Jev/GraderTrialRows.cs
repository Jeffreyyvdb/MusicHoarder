using System.Text.Json;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Quality.Jev;

/// <summary>Builds <see cref="GraderComparisonRun"/> rows, so every writer fills the same columns the same way.</summary>
public static class GraderTrialRows
{
    private static readonly JsonSerializerOptions CompactJson = new(JsonSerializerDefaults.Web);

    /// <summary>The incumbent's answer — a grade it just wrote, or failed to write.</summary>
    public static GraderComparisonRun Incumbent(
        GraderSubjectKind kind, int subjectId, Guid ownerUserId, string model, int promptVersion, string fingerprint,
        GradingResult? result, GradingCallStats? stats, int? latencyMs, string? errorCode, string? error,
        GraderRunSource source = GraderRunSource.Live) => new()
    {
        SubjectKind = kind,
        SubjectId = subjectId,
        OwnerUserId = ownerUserId,
        Arm = GraderArm.Incumbent,
        Source = source,
        Model = Clip(model, 128) ?? "",
        Version = promptVersion,
        InputFingerprint = fingerprint,
        Verdict = result?.Verdict,
        Score = result?.Score,
        UnsupportedIdentity = result is null || kind != GraderSubjectKind.Song
            ? null
            : result.Issues.Any(i => i.Code == "unsupported_identity") ? 1 : 0,
        IssueCodesJson = result is null ? null : Codes(result.Issues),
        Summary = Clip(result?.Summary, 1024),
        LatencyMs = latencyMs,
        Attempts = stats?.Attempts,
        InputTokens = stats?.PromptTokens,
        OutputTokens = stats?.CompletionTokens,
        CostUsd = stats?.CostUsd,
        ErrorCode = Clip(errorCode, 32),
        Error = Clip(error, 512),
        CreatedAtUtc = DateTime.UtcNow,
    };

    /// <summary>The incumbent's answer, copied from a grade that was written before the trial recorded it.</summary>
    public static GraderComparisonRun FromSongGrade(SongQualityGrade g) => Incumbent(
        GraderSubjectKind.Song, g.SongId, g.OwnerUserId, g.Model ?? "", g.PromptVersion, g.InputFingerprint ?? "",
        new GradingResult(g.Score, g.Verdict, g.Summary, GradingIssueJson.Parse(g.IssuesJson)),
        stats: null, g.DurationMs, null, null, GraderRunSource.Backfill);

    public static GraderComparisonRun FromAlbumGrade(CanonicalAlbumQualityGrade g) => Incumbent(
        GraderSubjectKind.Album, g.CanonicalAlbumId, g.OwnerUserId, g.Model ?? "", g.PromptVersion, g.InputFingerprint ?? "",
        new GradingResult(g.Score, g.Verdict, g.Summary, GradingIssueJson.Parse(g.IssuesJson)),
        stats: null, g.DurationMs, null, null, GraderRunSource.Backfill);

    /// <summary>A decision model's answer, or its failure when <paramref name="grade"/> is null.</summary>
    public static GraderComparisonRun Decision(
        GraderSubjectKind kind, int subjectId, Guid ownerUserId, string model, int questionVersion, string fingerprint,
        JevState state, JevGrade? grade, DecisionResult? result, int latencyMs, int? attempts,
        string? errorCode, string? error, GraderRunSource source) => new()
    {
        SubjectKind = kind,
        SubjectId = subjectId,
        OwnerUserId = ownerUserId,
        Arm = GraderArm.Decision,
        Source = source,
        Model = Clip(model, 128) ?? "",
        Version = questionVersion,
        InputFingerprint = fingerprint,
        FullStateBytes = state.FullBytes,
        StateBytes = state.SentBytes,
        StateShed = state.Shed,
        Verdict = grade?.Verdict,
        Score = grade?.Score,
        FlagProbability = grade?.FlagProbability,
        UnsupportedIdentity = grade?.UnsupportedIdentity,
        IssueCodesJson = grade is null ? null : Codes(grade.Issues),
        ProbabilitiesJson = grade?.ProbabilitiesJson,
        Summary = Clip(grade?.Summary, 1024),
        LatencyMs = latencyMs,
        Attempts = attempts,
        InputTokens = result?.InputTokens,
        OutputTokens = result?.OutputTokens,
        CostUsd = result?.CostUsd,
        ErrorCode = Clip(errorCode, 32),
        Error = Clip(error, 512),
        CreatedAtUtc = DateTime.UtcNow,
    };

    private static string Codes(IEnumerable<GradingIssue> issues) =>
        JsonSerializer.Serialize(issues.Select(i => i.Code).Distinct().ToList(), CompactJson);

    private static string? Clip(string? s, int max) => s is not null && s.Length > max ? s[..max] : s;
}
