using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Quality.Jev;

public sealed record TrialArmAnswer(
    string Model,
    string? Verdict,
    int? Score,
    double? FlagProbability,
    double? UnsupportedIdentity,
    string? Summary,
    IReadOnlyList<string> Issues,
    int? LatencyMs,
    decimal? CostUsd);

public sealed record TrialQueueItem(
    string Kind,
    int SubjectId,
    string Fingerprint,
    string Stratum,
    string? Title,
    string? Artist,
    string? Album,
    string? FileName,
    TrialArmAnswer Incumbent,
    TrialArmAnswer Challenger);

public sealed record TrialLabelRequest(
    string Kind,
    int SubjectId,
    string Fingerprint,
    string Label,
    bool UnsupportedIdentity);

/// <summary>
/// The grader trial's read side over the database: the report, the labelling queue, labels and the
/// CSV export. Reads through the request's tenancy filter, so an admin sees their own library's trial.
/// </summary>
public sealed class GraderTrialService(
    MusicHoarderDbContext db,
    IOptionsMonitor<JevGradingOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<GraderTrialReportDto> BuildReportAsync(GraderSubjectKind kind, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        var rows = await LoadRowsAsync(kind, ct);
        var labels = await LoadLabelsAsync(kind, ct);
        return GraderTrialReport.Build(kind, rows, labels, opts.PrimaryModel, opts.MaxStateBytes, DateTime.UtcNow);
    }

    /// <summary>
    /// Unlabelled pairs of the primary decision model and the incumbent: two disagreements for every
    /// agreement, the agreements in a fixed pseudo-random order so the sample does not favour recent
    /// or low-id subjects.
    /// </summary>
    public async Task<IReadOnlyList<TrialQueueItem>> LabelQueueAsync(GraderSubjectKind kind, int limit, CancellationToken ct)
    {
        var model = options.CurrentValue.PrimaryModel;
        if (model is null) return [];

        var rows = await LoadRowsAsync(kind, ct);
        var labelled = (await LoadLabelsAsync(kind, ct)).Select(l => (l.SubjectId, l.Fingerprint)).ToHashSet();
        var pairs = GraderTrialReport.Pairs(rows, GraderTrialReport.IncumbentAnswers(rows), model)
            .Where(p => !labelled.Contains((p.Challenger.SubjectId, p.Challenger.Fingerprint)))
            .ToList();

        var disagree = pairs.Where(IsDisagreement).OrderByDescending(p => p.Challenger.CreatedAtUtc).ToList();
        var agree = pairs.Where(p => !IsDisagreement(p)).OrderBy(p => StableShuffleKey(p.Challenger.SubjectId)).ToList();

        var picked = new List<(GraderTrialReport.Pair Pair, GraderTrialStratum Stratum)>();
        int d = 0, a = 0;
        while (picked.Count < limit && (d < disagree.Count || a < agree.Count))
        {
            // Two disagreements, then one agreement, while both last.
            var takeAgreement = (picked.Count % 3 == 2 && a < agree.Count) || d >= disagree.Count;
            if (takeAgreement) picked.Add((agree[a++], GraderTrialStratum.Agreement));
            else picked.Add((disagree[d++], GraderTrialStratum.Disagreement));
        }

        var raw = await db.GraderComparisonRuns.AsNoTracking()
            .Where(r => r.SubjectKind == kind)
            .Select(r => new { r.SubjectId, r.InputFingerprint, r.Arm, r.Model, r.CreatedAtUtc, r.Summary, r.IssueCodesJson })
            .ToListAsync(ct);
        var details = raw.ToLookup(r => (r.SubjectId, r.InputFingerprint, r.Arm, r.Model));

        var ids = picked.Select(p => p.Pair.Challenger.SubjectId).Distinct().ToList();
        var names = await SubjectNamesAsync(kind, ids, ct);

        TrialArmAnswer Answer(TrialRow r)
        {
            var extra = details[(r.SubjectId, r.Fingerprint, r.Arm, r.Model)]
                .OrderBy(x => Math.Abs((x.CreatedAtUtc - r.CreatedAtUtc).Ticks)).FirstOrDefault();
            return new TrialArmAnswer(r.Model, r.Verdict?.ToString(), r.Score, r.FlagProbability, r.UnsupportedIdentity,
                extra?.Summary, ParseCodes(extra?.IssueCodesJson), r.LatencyMs, r.CostUsd);
        }

        return picked.Select(p =>
        {
            names.TryGetValue(p.Pair.Challenger.SubjectId, out var n);
            return new TrialQueueItem(
                kind.ToString(), p.Pair.Challenger.SubjectId, p.Pair.Challenger.Fingerprint, p.Stratum.ToString(),
                n?.Title, n?.Artist, n?.Album, n?.FileName,
                Answer(p.Pair.Incumbent), Answer(p.Pair.Challenger));
        }).ToList();
    }

    /// <summary>
    /// Saves (or replaces) a label. The stratum is worked out here from the primary pair rather than
    /// taken from the client, because the report's weights depend on it. Null when there is no such pair.
    /// </summary>
    public async Task<GraderTrialLabel?> SaveLabelAsync(GraderSubjectKind kind, TrialLabelRequest request, GraderTrialLabelValue value, CancellationToken ct)
    {
        var model = options.CurrentValue.PrimaryModel;
        if (model is null) return null;

        var rows = await LoadRowsAsync(kind, ct, request.SubjectId);
        var pair = GraderTrialReport.Pairs(rows, GraderTrialReport.IncumbentAnswers(rows), model)
            .FirstOrDefault(p => p.Challenger.Fingerprint == request.Fingerprint);
        if (pair is null) return null;

        var owner = await db.GraderComparisonRuns
            .Where(r => r.SubjectKind == kind && r.SubjectId == request.SubjectId && r.InputFingerprint == request.Fingerprint)
            .Select(r => (Guid?)r.OwnerUserId)
            .FirstOrDefaultAsync(ct);
        if (owner is null) return null;

        var label = await db.GraderTrialLabels.FirstOrDefaultAsync(l =>
            l.SubjectKind == kind && l.SubjectId == request.SubjectId && l.InputFingerprint == request.Fingerprint, ct);
        if (label is null)
        {
            label = new GraderTrialLabel
            {
                SubjectKind = kind,
                SubjectId = request.SubjectId,
                OwnerUserId = owner.Value,
                InputFingerprint = request.Fingerprint,
            };
            db.GraderTrialLabels.Add(label);
        }

        label.Label = value;
        label.UnsupportedIdentity = value == GraderTrialLabelValue.Wrong && request.UnsupportedIdentity;
        label.Stratum = IsDisagreement(pair) ? GraderTrialStratum.Disagreement : GraderTrialStratum.Agreement;
        label.LabeledAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return label;
    }

    /// <summary>One CSV line per pair per decision model, with the human label when there is one.</summary>
    public async Task<string> ExportCsvAsync(GraderSubjectKind kind, CancellationToken ct)
    {
        var rows = await LoadRowsAsync(kind, ct);
        var labels = (await LoadLabelsAsync(kind, ct)).ToDictionary(l => (l.SubjectId, l.Fingerprint));
        var incumbent = GraderTrialReport.IncumbentAnswers(rows);
        var models = rows.Where(r => r.Arm == GraderArm.Decision).Select(r => r.Model).Distinct().OrderBy(m => m, StringComparer.Ordinal);

        var sb = new StringBuilder();
        sb.AppendLine("kind,subject_id,fingerprint,incumbent_model,incumbent_verdict,incumbent_score,incumbent_latency_ms,incumbent_cost_usd,"
            + "decision_model,decision_verdict,decision_score,flag_probability,unsupported_identity,decision_latency_ms,decision_cost_usd,"
            + "state_bytes,state_shed,label,label_unsupported_identity,label_stratum");
        foreach (var model in models)
        foreach (var p in GraderTrialReport.Pairs(rows, incumbent, model))
        {
            labels.TryGetValue((p.Challenger.SubjectId, p.Challenger.Fingerprint), out var l);
            sb.AppendJoin(',',
                kind, p.Challenger.SubjectId, p.Challenger.Fingerprint,
                Csv(p.Incumbent.Model), p.Incumbent.Verdict, p.Incumbent.Score, p.Incumbent.LatencyMs, Num(p.Incumbent.CostUsd),
                Csv(model), p.Challenger.Verdict, p.Challenger.Score, Num(p.Challenger.FlagProbability), Num(p.Challenger.UnsupportedIdentity),
                p.Challenger.LatencyMs, Num(p.Challenger.CostUsd), p.Challenger.StateBytes, p.Challenger.StateShed ? "1" : "0",
                l?.Label.ToString(), l is null ? "" : l.UnsupportedIdentity ? "1" : "0", l?.Stratum.ToString());
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static bool IsDisagreement(GraderTrialReport.Pair p) =>
        GraderTrialReport.IsFlagged(p.Incumbent.Verdict!.Value) != GraderTrialReport.IsFlagged(p.Challenger.Verdict!.Value);

    // Knuth's multiplicative hash: a fixed, id-scattering order (not HashCode, which is seeded per process).
    private static uint StableShuffleKey(int id) => unchecked((uint)id * 2654435761u);

    private async Task<List<TrialRow>> LoadRowsAsync(GraderSubjectKind kind, CancellationToken ct, int? subjectId = null)
    {
        var query = db.GraderComparisonRuns.AsNoTracking().Where(r => r.SubjectKind == kind);
        if (subjectId is { } id) query = query.Where(r => r.SubjectId == id);
        return await query
            .Select(r => new TrialRow(
                r.SubjectId, r.InputFingerprint, r.Arm, r.Model, r.Source, r.Verdict, r.Score,
                r.FlagProbability, r.UnsupportedIdentity, r.LatencyMs, r.Attempts, r.InputTokens, r.OutputTokens,
                r.CostUsd, r.ErrorCode, r.FullStateBytes, r.StateBytes, r.StateShed, r.CreatedAtUtc))
            .ToListAsync(ct);
    }

    private async Task<List<TrialLabel>> LoadLabelsAsync(GraderSubjectKind kind, CancellationToken ct) =>
        await db.GraderTrialLabels.AsNoTracking()
            .Where(l => l.SubjectKind == kind)
            .Select(l => new TrialLabel(l.SubjectId, l.InputFingerprint, l.Label, l.UnsupportedIdentity, l.Stratum))
            .ToListAsync(ct);

    private sealed record SubjectName(string? Title, string? Artist, string? Album, string? FileName);

    private async Task<Dictionary<int, SubjectName>> SubjectNamesAsync(GraderSubjectKind kind, List<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        if (kind == GraderSubjectKind.Song)
            return await db.Songs.AsNoTracking()
                .Where(s => ids.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => new SubjectName(s.Title, s.Artist, s.Album, s.FileName), ct);

        return await db.CanonicalAlbums.AsNoTracking()
            .Where(a => ids.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => new SubjectName(null, a.DisplayArtist, a.DisplayTitle, null), ct);
    }

    private static IReadOnlyList<string> ParseCodes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string Csv(string? s) =>
        s is null ? "" : s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

    private static string Num(double? v) => v?.ToString("0.####", CultureInfo.InvariantCulture) ?? "";

    private static string Num(decimal? v) => v?.ToString("0.#########", CultureInfo.InvariantCulture) ?? "";
}
