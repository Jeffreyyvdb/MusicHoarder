using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Quality.Jev;

/// <summary>One trial answer, as the report reads it.</summary>
public sealed record TrialRow(
    int SubjectId,
    string Fingerprint,
    GraderArm Arm,
    string Model,
    GraderRunSource Source,
    SongQualityVerdict? Verdict,
    int? Score,
    double? FlagProbability,
    double? UnsupportedIdentity,
    int? LatencyMs,
    int? Attempts,
    int? InputTokens,
    int? OutputTokens,
    decimal? CostUsd,
    string? ErrorCode,
    int? FullStateBytes,
    int? StateBytes,
    bool StateShed,
    DateTime CreatedAtUtc)
{
    public bool Answered => ErrorCode is null && Verdict is not null;
}

public sealed record TrialLabel(
    int SubjectId,
    string Fingerprint,
    GraderTrialLabelValue Label,
    bool UnsupportedIdentity,
    GraderTrialStratum Stratum);

public sealed record ArmKey(GraderArm Arm, string Model)
{
    public override string ToString() => Arm == GraderArm.Incumbent ? $"incumbent:{Model}" : Model;
}

public sealed record ArmSummary(
    string Arm,
    string Model,
    bool IsIncumbent,
    int Answers,
    int Errors,
    double NoGradeRate,
    int? LatencyP50Ms,
    int? LatencyP95Ms,
    double? AverageAttempts,
    int CostKnown,
    decimal? CostPerSubjectUsd,
    decimal? CostPer1000Usd,
    double? AverageInputTokens,
    double? AverageOutputTokens,
    CoverageSummary? Coverage,
    StabilitySummary Stability);

public sealed record CoverageSummary(int Requests, double FitWithoutShedding, double FitAfterShedding, double AverageStateBytes, int MaxStateBytes);

public sealed record StabilitySummary(int Pairs, int Flips, double? FlipRate);

public sealed record QualitySummary(
    int Labelled,
    int LabelledWrong,
    double? Precision,
    double? Recall,
    int UnsupportedLabelled,
    double? UnsupportedRecall,
    double? Brier,
    IReadOnlyList<CalibrationBin> Calibration);

public sealed record CalibrationBin(double From, double To, int Count, double? MeanPredicted, double? Observed);

public sealed record DecisionCheck(string Name, string Status, string Detail);

public sealed record ArmComparison(
    string Arm,
    string Model,
    int Pairs,
    /// <summary>Rows: incumbent verdict; columns: this arm's verdict; both in <see cref="GraderTrialReport.VerdictOrder"/>.</summary>
    int[][] Confusion,
    double? Kappa,
    double? ScoreMae,
    double? FlagAgreement,
    int Disagreements,
    QualitySummary Incumbent,
    QualitySummary Challenger,
    IReadOnlyList<DecisionCheck> Checks,
    string Recommendation);

public sealed record GraderTrialReportDto(
    string Kind,
    DateTime GeneratedAtUtc,
    string? PrimaryModel,
    IReadOnlyList<string> VerdictOrder,
    IReadOnlyList<ArmSummary> Arms,
    IReadOnlyList<ArmComparison> Comparisons,
    LabelProgress Labels);

public sealed record LabelProgress(int Labelled, int Disagreements, int DisagreementsLabelled, int Agreements, int AgreementsLabelled);

/// <summary>
/// The trial's arithmetic, kept free of EF so it can be tested against hand-worked numbers. A pair
/// is the incumbent's answer and one decision model's answer for the same subject on the same
/// dossier; "flagged" means the AI-flagged queue would hold it (wrong or questionable).
/// <para>
/// Labels oversample the pairs where the primary decision model and the incumbent disagree, so
/// precision, recall and the Brier score weight each label by its stratum's share of all pairs
/// (pairs in the stratum ÷ labels in it). Unsure labels are left out.
/// </para>
/// </summary>
public static class GraderTrialReport
{
    public static readonly SongQualityVerdict[] VerdictOrder =
    [
        SongQualityVerdict.Excellent, SongQualityVerdict.Good, SongQualityVerdict.Questionable,
        SongQualityVerdict.Wrong, SongQualityVerdict.Ungradeable,
    ];

    /// <summary>Labelled "wrong" subjects needed before the quality checks pass or fail rather than say "not enough data".</summary>
    public const int MinWrongLabels = 20;

    /// <summary>Labelled unsupported-identity subjects needed for that check.</summary>
    public const int MinUnsupportedLabels = 5;

    /// <summary>Priced grades needed on each side before the cost check decides.</summary>
    public const int MinPricedGrades = 20;

    /// <summary>Decision answers needed before the latency and coverage checks decide.</summary>
    public const int MinAnswers = 20;

    /// <summary>How far below the incumbent's recall the challenger may fall (2 points) and still pass.</summary>
    public const double RecallTolerance = 0.02;

    public static bool IsFlagged(SongQualityVerdict v) => v is SongQualityVerdict.Wrong or SongQualityVerdict.Questionable;

    public static GraderTrialReportDto Build(
        GraderSubjectKind kind,
        IReadOnlyList<TrialRow> rows,
        IReadOnlyList<TrialLabel> labels,
        string? primaryModel,
        int maxStateBytes,
        DateTime now)
    {
        var arms = rows.Select(r => new ArmKey(r.Arm, r.Model)).Distinct()
            .OrderBy(a => a.Arm).ThenBy(a => a.Model, StringComparer.Ordinal).ToList();
        var summaries = arms.Select(a => Summarize(a, rows.Where(r => r.Arm == a.Arm && r.Model == a.Model).ToList(), maxStateBytes)).ToList();

        var incumbentByInput = IncumbentAnswers(rows);
        var labelByInput = labels
            .Where(l => l.Label != GraderTrialLabelValue.Unsure)
            .ToDictionary(l => (l.SubjectId, l.Fingerprint));

        // Weights come from the primary model's pairs, the population the labels were drawn from.
        var primaryPairs = primaryModel is null ? [] : Pairs(rows, incumbentByInput, primaryModel);
        var weights = StratumWeights(primaryPairs, labelByInput.Values);

        var incumbentSummaries = summaries.Where(s => s.IsIncumbent).ToList();
        var comparisons = arms.Where(a => a.Arm == GraderArm.Decision)
            .Select(a => Compare(a, Pairs(rows, incumbentByInput, a.Model), labelByInput, weights,
                summaries.First(s => !s.IsIncumbent && s.Model == a.Model), incumbentSummaries))
            .ToList();

        var disagreements = primaryPairs.Count(p => IsFlagged(p.Incumbent.Verdict!.Value) != IsFlagged(p.Challenger.Verdict!.Value));
        var labelled = labels.Count;
        var progress = new LabelProgress(
            labelled,
            disagreements,
            labels.Count(l => l.Stratum == GraderTrialStratum.Disagreement),
            primaryPairs.Count - disagreements,
            labels.Count(l => l.Stratum == GraderTrialStratum.Agreement));

        return new GraderTrialReportDto(
            kind.ToString(), now, primaryModel,
            VerdictOrder.Select(v => v.ToString()).ToList(),
            summaries, comparisons, progress);
    }

    public sealed record Pair(TrialRow Incumbent, TrialRow Challenger);

    /// <summary>The incumbent's answer per input: the newest answered row, ignoring stability repeats.</summary>
    public static Dictionary<(int, string), TrialRow> IncumbentAnswers(IEnumerable<TrialRow> rows) =>
        rows.Where(r => r.Arm == GraderArm.Incumbent && r.Source != GraderRunSource.Stability && r.Answered)
            .GroupBy(r => (r.SubjectId, r.Fingerprint))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.CreatedAtUtc).First());

    /// <summary>Pairs of one decision model with the incumbent on the same input (first answer, ignoring stability repeats).</summary>
    public static List<Pair> Pairs(IEnumerable<TrialRow> rows, Dictionary<(int, string), TrialRow> incumbent, string model) =>
        rows.Where(r => r.Arm == GraderArm.Decision && r.Model == model && r.Source != GraderRunSource.Stability && r.Answered)
            .GroupBy(r => (r.SubjectId, r.Fingerprint))
            .Select(g => (Key: g.Key, Row: g.OrderBy(r => r.CreatedAtUtc).First()))
            .Where(x => incumbent.ContainsKey(x.Key))
            .Select(x => new Pair(incumbent[x.Key], x.Row))
            .ToList();

    private static Dictionary<GraderTrialStratum, double> StratumWeights(IReadOnlyList<Pair> population, IEnumerable<TrialLabel> labels)
    {
        var popDisagree = population.Count(p => IsFlagged(p.Incumbent.Verdict!.Value) != IsFlagged(p.Challenger.Verdict!.Value));
        var popAgree = population.Count - popDisagree;
        var labelList = labels.ToList();
        var nDisagree = labelList.Count(l => l.Stratum == GraderTrialStratum.Disagreement);
        var nAgree = labelList.Count(l => l.Stratum == GraderTrialStratum.Agreement);
        return new Dictionary<GraderTrialStratum, double>
        {
            [GraderTrialStratum.Disagreement] = nDisagree > 0 ? (double)popDisagree / nDisagree : 0,
            [GraderTrialStratum.Agreement] = nAgree > 0 ? (double)popAgree / nAgree : 0,
        };
    }

    private static ArmSummary Summarize(ArmKey arm, List<TrialRow> all, int maxStateBytes)
    {
        var rows = all.Where(r => r.Source != GraderRunSource.Stability).ToList();
        var answered = rows.Where(r => r.Answered).ToList();
        var errors = rows.Count - answered.Count;
        var latencies = rows.Where(r => r.LatencyMs is not null).Select(r => r.LatencyMs!.Value).OrderBy(x => x).ToList();
        var priced = rows.Where(r => r.CostUsd is not null).ToList();
        decimal? perSubject = priced.Count > 0 ? priced.Average(r => r.CostUsd!.Value) : null;
        var attempts = rows.Where(r => r.Attempts is not null).ToList();
        var inTokens = rows.Where(r => r.InputTokens is not null).ToList();
        var outTokens = rows.Where(r => r.OutputTokens is not null).ToList();

        CoverageSummary? coverage = null;
        if (arm.Arm == GraderArm.Decision)
        {
            var sized = rows.Where(r => r.FullStateBytes is not null && r.StateBytes is not null).ToList();
            coverage = sized.Count == 0
                ? new CoverageSummary(0, 0, 0, 0, maxStateBytes)
                : new CoverageSummary(
                    sized.Count,
                    sized.Count(r => r.FullStateBytes <= maxStateBytes) / (double)sized.Count,
                    sized.Count(r => r.StateBytes <= maxStateBytes) / (double)sized.Count,
                    sized.Average(r => r.StateBytes!.Value),
                    maxStateBytes);
        }

        return new ArmSummary(
            arm.ToString(), arm.Model, arm.Arm == GraderArm.Incumbent,
            answered.Count, errors,
            rows.Count > 0 ? errors / (double)rows.Count : 0,
            Percentile(latencies, 0.50), Percentile(latencies, 0.95),
            attempts.Count > 0 ? attempts.Average(r => r.Attempts!.Value) : null,
            priced.Count, perSubject, perSubject * 1000,
            inTokens.Count > 0 ? inTokens.Average(r => r.InputTokens!.Value) : null,
            outTokens.Count > 0 ? outTokens.Average(r => r.OutputTokens!.Value) : null,
            coverage,
            Stability(all));
    }

    /// <summary>Among inputs answered twice by this arm, how often the second verdict differs from the first.</summary>
    public static StabilitySummary Stability(IEnumerable<TrialRow> armRows)
    {
        var repeated = armRows.Where(r => r.Answered)
            .GroupBy(r => (r.SubjectId, r.Fingerprint))
            .Where(g => g.Count() >= 2 && g.Any(r => r.Source == GraderRunSource.Stability))
            .Select(g => g.OrderBy(r => r.CreatedAtUtc).Take(2).ToList())
            .ToList();
        var flips = repeated.Count(p => p[0].Verdict != p[1].Verdict);
        return new StabilitySummary(repeated.Count, flips, repeated.Count > 0 ? flips / (double)repeated.Count : null);
    }

    private static ArmComparison Compare(
        ArmKey arm, List<Pair> pairs,
        Dictionary<(int, string), TrialLabel> labels,
        Dictionary<GraderTrialStratum, double> weights,
        ArmSummary challengerSummary,
        IReadOnlyList<ArmSummary> incumbentSummaries)
    {
        var confusion = VerdictOrder.Select(_ => new int[VerdictOrder.Length]).ToArray();
        foreach (var p in pairs)
            confusion[Array.IndexOf(VerdictOrder, p.Incumbent.Verdict!.Value)][Array.IndexOf(VerdictOrder, p.Challenger.Verdict!.Value)]++;

        var scored = pairs.Where(p => p.Incumbent.Score is not null && p.Challenger.Score is not null).ToList();
        double? mae = scored.Count > 0 ? scored.Average(p => Math.Abs(p.Incumbent.Score!.Value - p.Challenger.Score!.Value)) : null;
        var agreeFlag = pairs.Count(p => IsFlagged(p.Incumbent.Verdict!.Value) == IsFlagged(p.Challenger.Verdict!.Value));

        var labelled = pairs
            .Where(p => labels.ContainsKey((p.Challenger.SubjectId, p.Challenger.Fingerprint)))
            .Select(p => (Pair: p, Label: labels[(p.Challenger.SubjectId, p.Challenger.Fingerprint)]))
            .ToList();

        var incumbentQuality = Quality(labelled.Select(x => (x.Pair.Incumbent, x.Label)).ToList(), weights, probabilities: false);
        var challengerQuality = Quality(labelled.Select(x => (x.Pair.Challenger, x.Label)).ToList(), weights, probabilities: true);

        // The incumbent's speed and price: its model that answered the most pairs (normally the only one).
        var incumbentModel = pairs.GroupBy(p => p.Incumbent.Model).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        var incumbentSummary = incumbentSummaries.FirstOrDefault(s => s.Model == incumbentModel);

        var checks = Checks(incumbentQuality, challengerQuality, incumbentSummary, challengerSummary, pairs);
        return new ArmComparison(
            arm.ToString(), arm.Model, pairs.Count, confusion,
            Kappa(confusion), mae,
            pairs.Count > 0 ? agreeFlag / (double)pairs.Count : null,
            pairs.Count - agreeFlag,
            incumbentQuality, challengerQuality, checks, Recommend(checks));
    }

    /// <summary>Weighted precision and recall of "flagged" against "labelled wrong", plus the Brier score and calibration of the flag probability.</summary>
    public static QualitySummary Quality(
        IReadOnlyList<(TrialRow Row, TrialLabel Label)> labelled,
        IReadOnlyDictionary<GraderTrialStratum, double> weights,
        bool probabilities)
    {
        double tp = 0, flagged = 0, wrong = 0;
        double unsupported = 0, unsupportedCaught = 0;
        double brierSum = 0, brierWeight = 0;
        var bins = Enumerable.Range(0, 5).Select(_ => (W: 0.0, Pred: 0.0, Obs: 0.0, N: 0)).ToArray();

        foreach (var (row, label) in labelled)
        {
            var w = weights.GetValueOrDefault(label.Stratum);
            if (w <= 0) continue;
            var isWrong = label.Label == GraderTrialLabelValue.Wrong;
            var isFlagged = IsFlagged(row.Verdict!.Value);
            if (isFlagged) flagged += w;
            if (isWrong) wrong += w;
            if (isFlagged && isWrong) tp += w;
            if (isWrong && label.UnsupportedIdentity)
            {
                unsupported += w;
                if (isFlagged) unsupportedCaught += w;
            }

            if (probabilities && row.FlagProbability is { } p)
            {
                var y = isWrong ? 1.0 : 0.0;
                brierSum += w * (p - y) * (p - y);
                brierWeight += w;
                var b = Math.Min(4, (int)(p * 5));
                bins[b] = (bins[b].W + w, bins[b].Pred + w * p, bins[b].Obs + w * y, bins[b].N + 1);
            }
        }

        var calibration = probabilities
            ? bins.Select((b, i) => new CalibrationBin(i / 5.0, (i + 1) / 5.0, b.N,
                b.W > 0 ? b.Pred / b.W : null, b.W > 0 ? b.Obs / b.W : null)).ToList()
            : [];

        return new QualitySummary(
            labelled.Count,
            labelled.Count(x => x.Label.Label == GraderTrialLabelValue.Wrong),
            flagged > 0 ? tp / flagged : null,
            wrong > 0 ? tp / wrong : null,
            labelled.Count(x => x.Label.Label == GraderTrialLabelValue.Wrong && x.Label.UnsupportedIdentity),
            unsupported > 0 ? unsupportedCaught / unsupported : null,
            brierWeight > 0 ? brierSum / brierWeight : null,
            calibration);
    }

    /// <summary>Cohen's kappa over a square confusion matrix; null when there is nothing to compare or agreement by chance is total.</summary>
    public static double? Kappa(int[][] m)
    {
        var n = m.Sum(r => r.Sum());
        if (n == 0) return null;
        var k = m.Length;
        double observed = 0, expected = 0;
        for (var i = 0; i < k; i++)
        {
            observed += m[i][i];
            var row = m[i].Sum();
            var col = m.Sum(r => r[i]);
            expected += (double)row * col;
        }
        observed /= n;
        expected /= (double)n * n;
        return expected >= 1 ? null : (observed - expected) / (1 - expected);
    }

    /// <summary>Nearest-rank percentile of an ascending list.</summary>
    public static int? Percentile(IReadOnlyList<int> sorted, double q)
    {
        if (sorted.Count == 0) return null;
        var rank = (int)Math.Ceiling(q * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }

    private const string Pass = "pass", Fail = "fail", Insufficient = "insufficient";

    /// <summary>The decision rule, fixed before results came in (see the trial plan).</summary>
    private static List<DecisionCheck> Checks(
        QualitySummary incumbent, QualitySummary challenger, ArmSummary? incumbentArm, ArmSummary challengerArm, List<Pair> pairs)
    {
        var checks = new List<DecisionCheck>();

        // 1. Recall within 2 points, precision no worse.
        if (challenger.LabelledWrong < MinWrongLabels || incumbent.Recall is null || challenger.Recall is null)
            checks.Add(new("quality", Insufficient, $"{challenger.LabelledWrong} of {MinWrongLabels} labelled wrong subjects needed"));
        else
        {
            var ok = challenger.Recall >= incumbent.Recall - RecallTolerance
                && (challenger.Precision ?? 0) >= (incumbent.Precision ?? 0);
            checks.Add(new("quality", ok ? Pass : Fail,
                $"recall {Pct(challenger.Recall)} vs {Pct(incumbent.Recall)}, precision {Pct(challenger.Precision)} vs {Pct(incumbent.Precision)}"));
        }

        // 2. Unsupported identity caught at least as often.
        if (challenger.UnsupportedLabelled < MinUnsupportedLabels || incumbent.UnsupportedRecall is null || challenger.UnsupportedRecall is null)
            checks.Add(new("unsupported_identity", Insufficient, $"{challenger.UnsupportedLabelled} of {MinUnsupportedLabels} labelled unsupported-identity subjects needed"));
        else
            checks.Add(new("unsupported_identity", challenger.UnsupportedRecall >= incumbent.UnsupportedRecall ? Pass : Fail,
                $"recall {Pct(challenger.UnsupportedRecall)} vs {Pct(incumbent.UnsupportedRecall)}"));

        // 3. Faster at p95 and cheaper per 1,000, retries included.
        if (incumbentArm is null || challengerArm.Answers < MinAnswers || incumbentArm.LatencyP95Ms is null || challengerArm.LatencyP95Ms is null)
            checks.Add(new("latency", Insufficient, $"{challengerArm.Answers} of {MinAnswers} answers needed"));
        else
            checks.Add(new("latency", challengerArm.LatencyP95Ms < incumbentArm.LatencyP95Ms ? Pass : Fail,
                $"p95 {challengerArm.LatencyP95Ms} ms vs {incumbentArm.LatencyP95Ms} ms"));

        if (incumbentArm is null || incumbentArm.CostKnown < MinPricedGrades || challengerArm.CostKnown < MinPricedGrades)
            checks.Add(new("cost", Insufficient,
                $"priced grades: {challengerArm.CostKnown} decision, {incumbentArm?.CostKnown ?? 0} incumbent; {MinPricedGrades} each needed"));
        else
            checks.Add(new("cost", challengerArm.CostPer1000Usd < incumbentArm.CostPer1000Usd ? Pass : Fail,
                $"${challengerArm.CostPer1000Usd:0.####} vs ${incumbentArm.CostPer1000Usd:0.####} per 1,000"));

        // 4. Dossiers fit, or shedding them does not cost agreement.
        var cov = challengerArm.Coverage;
        if (cov is null || cov.Requests < MinAnswers)
            checks.Add(new("coverage", Insufficient, $"{cov?.Requests ?? 0} of {MinAnswers} requests needed"));
        else if (cov.FitWithoutShedding >= 0.95)
            checks.Add(new("coverage", Pass, $"{Pct(cov.FitWithoutShedding)} fit without shedding"));
        else
        {
            var shed = pairs.Where(p => p.Challenger.StateShed).ToList();
            var whole = pairs.Where(p => !p.Challenger.StateShed).ToList();
            double Agree(List<Pair> ps) => ps.Count == 0 ? 0 : ps.Count(p => IsFlagged(p.Incumbent.Verdict!.Value) == IsFlagged(p.Challenger.Verdict!.Value)) / (double)ps.Count;
            var ok = cov.FitAfterShedding >= 0.95 && shed.Count > 0 && whole.Count > 0 && Agree(shed) >= Agree(whole);
            checks.Add(new("coverage", ok ? Pass : Fail,
                $"{Pct(cov.FitWithoutShedding)} fit without shedding, {Pct(cov.FitAfterShedding)} after; flag agreement {Pct(Agree(shed))} shed vs {Pct(Agree(whole))} whole"));
        }

        return checks;
    }

    private static string Recommend(IReadOnlyList<DecisionCheck> checks)
    {
        string Status(string name) => checks.First(c => c.Name == name).Status;
        var quality = new[] { Status("quality"), Status("unsupported_identity") };
        var costSpeed = new[] { Status("latency"), Status("cost"), Status("coverage") };

        if (quality.Contains(Fail)) return "keep-incumbent";
        if (quality.Contains(Insufficient)) return "need-more-data";
        if (costSpeed.All(s => s == Pass)) return "switch";
        if (costSpeed.Contains(Insufficient)) return "need-more-data";
        return "rerun-with-fewer-questions";
    }

    private static string Pct(double? v) => v is null ? "—" : $"{v.Value * 100:0.#}%";
}
