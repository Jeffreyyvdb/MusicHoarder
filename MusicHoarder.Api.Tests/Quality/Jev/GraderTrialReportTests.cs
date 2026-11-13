using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Quality.Jev;

namespace MusicHoarder.Api.Tests.Quality.Jev;

public class GraderTrialReportTests
{
    private const string Jev = "typesafe/jev-1.14-20261014";
    private const string Chat = "deepseek/deepseek-v4-flash";
    private static readonly DateTime T0 = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Kappa_matches_a_hand_worked_example()
    {
        // 50 pairs: observed agreement 35/50 = 0.70; chance agreement (25·30 + 25·20)/50² = 0.50;
        // kappa = (0.70 − 0.50) / (1 − 0.50) = 0.40.
        var m = Matrix();
        m[0][0] = 20; m[0][1] = 5;
        m[1][0] = 10; m[1][1] = 15;

        Assert.Equal(0.40, GraderTrialReport.Kappa(m)!.Value, 10);
    }

    [Fact]
    public void Kappa_is_null_without_pairs()
    {
        Assert.Null(GraderTrialReport.Kappa(Matrix()));
    }

    [Fact]
    public void Percentile_is_nearest_rank()
    {
        var xs = Enumerable.Range(1, 10).ToList();
        Assert.Equal(5, GraderTrialReport.Percentile(xs, 0.50));
        Assert.Equal(10, GraderTrialReport.Percentile(xs, 0.95));
        Assert.Null(GraderTrialReport.Percentile([], 0.5));
    }

    [Fact]
    public void Precision_and_recall_weight_each_label_by_its_stratum()
    {
        // Weights: disagreements 2 (10 pairs, 5 labels), agreements 10 (90 pairs, 9 labels).
        var weights = new Dictionary<GraderTrialStratum, double>
        {
            [GraderTrialStratum.Disagreement] = 2,
            [GraderTrialStratum.Agreement] = 10,
        };
        var labelled = new List<(TrialRow, TrialLabel)>();
        var id = 0;
        void Add(GraderTrialStratum s, bool flagged, bool wrong, int n)
        {
            for (var i = 0; i < n; i++, id++)
                labelled.Add((Row(id, GraderArm.Decision, Jev, flagged ? SongQualityVerdict.Wrong : SongQualityVerdict.Good),
                    new TrialLabel(id, $"f{id}", wrong ? GraderTrialLabelValue.Wrong : GraderTrialLabelValue.Right, false, s)));
        }
        Add(GraderTrialStratum.Disagreement, flagged: true, wrong: true, 3);
        Add(GraderTrialStratum.Disagreement, flagged: true, wrong: false, 1);
        Add(GraderTrialStratum.Disagreement, flagged: false, wrong: true, 1);
        Add(GraderTrialStratum.Agreement, flagged: true, wrong: true, 1);
        Add(GraderTrialStratum.Agreement, flagged: false, wrong: false, 7);
        Add(GraderTrialStratum.Agreement, flagged: false, wrong: true, 1);

        var q = GraderTrialReport.Quality(labelled, weights, probabilities: false);

        // tp = 3·2 + 1·10 = 16; flagged = 4·2 + 1·10 = 18; wrong = 4·2 + 2·10 = 28.
        Assert.Equal(16.0 / 18, q.Precision!.Value, 10);
        Assert.Equal(16.0 / 28, q.Recall!.Value, 10);
        Assert.Equal(14, q.Labelled);
        Assert.Equal(6, q.LabelledWrong);
    }

    [Fact]
    public void Brier_score_and_calibration_use_the_flag_probability()
    {
        var weights = new Dictionary<GraderTrialStratum, double> { [GraderTrialStratum.Agreement] = 1 };
        var labelled = new List<(TrialRow, TrialLabel)>
        {
            (Row(1, GraderArm.Decision, Jev, SongQualityVerdict.Wrong, flag: 0.9), new TrialLabel(1, "f1", GraderTrialLabelValue.Wrong, false, GraderTrialStratum.Agreement)),
            (Row(2, GraderArm.Decision, Jev, SongQualityVerdict.Good, flag: 0.2), new TrialLabel(2, "f2", GraderTrialLabelValue.Right, false, GraderTrialStratum.Agreement)),
        };

        var q = GraderTrialReport.Quality(labelled, weights, probabilities: true);

        // ((0.9 − 1)² + (0.2 − 0)²) / 2 = (0.01 + 0.04) / 2 = 0.025
        Assert.Equal(0.025, q.Brier!.Value, 10);
        Assert.Equal(1, q.Calibration[4].Count);     // 0.9 falls in 0.8–1.0
        Assert.Equal(1.0, q.Calibration[4].Observed);
        Assert.Equal(1, q.Calibration[1].Count);     // 0.2 falls in 0.2–0.4
        Assert.Equal(0.0, q.Calibration[1].Observed);
    }

    [Fact]
    public void Stability_counts_a_changed_verdict_on_the_same_input_as_a_flip()
    {
        var rows = new[]
        {
            Row(1, GraderArm.Decision, Jev, SongQualityVerdict.Good),
            Row(1, GraderArm.Decision, Jev, SongQualityVerdict.Questionable, source: GraderRunSource.Stability, at: T0.AddHours(1)),
            Row(2, GraderArm.Decision, Jev, SongQualityVerdict.Good),
            Row(2, GraderArm.Decision, Jev, SongQualityVerdict.Good, source: GraderRunSource.Stability, at: T0.AddHours(1)),
            Row(3, GraderArm.Decision, Jev, SongQualityVerdict.Good), // answered once: not a stability pair
        };

        var s = GraderTrialReport.Stability(rows);

        Assert.Equal(2, s.Pairs);
        Assert.Equal(1, s.Flips);
        Assert.Equal(0.5, s.FlipRate);
    }

    [Fact]
    public void Pairs_ignore_failed_answers_and_stability_repeats()
    {
        var rows = new List<TrialRow>
        {
            Row(1, GraderArm.Incumbent, Chat, SongQualityVerdict.Good),
            Row(1, GraderArm.Decision, Jev, SongQualityVerdict.Good),
            Row(1, GraderArm.Decision, Jev, SongQualityVerdict.Wrong, source: GraderRunSource.Stability, at: T0.AddHours(1)),
            Row(2, GraderArm.Incumbent, Chat, SongQualityVerdict.Wrong),
            Row(2, GraderArm.Decision, Jev, null, error: "http_error"),
            Row(3, GraderArm.Decision, Jev, SongQualityVerdict.Good), // no incumbent answer
        };

        var pairs = GraderTrialReport.Pairs(rows, GraderTrialReport.IncumbentAnswers(rows), Jev);

        var pair = Assert.Single(pairs);
        Assert.Equal(1, pair.Challenger.SubjectId);
        Assert.Equal(SongQualityVerdict.Good, pair.Challenger.Verdict);
    }

    [Fact]
    public void Report_without_labels_asks_for_more_data()
    {
        var rows = new List<TrialRow>
        {
            Row(1, GraderArm.Incumbent, Chat, SongQualityVerdict.Good, latency: 12_000, cost: 0.002m),
            Row(1, GraderArm.Decision, Jev, SongQualityVerdict.Wrong, latency: 150, cost: 0.0003m),
        };

        var report = GraderTrialReport.Build(GraderSubjectKind.Song, rows, [], Jev, 32_000, T0);

        var c = Assert.Single(report.Comparisons);
        Assert.Equal(1, c.Pairs);
        Assert.Equal(1, c.Disagreements);
        Assert.Equal(1, c.Confusion[1][3]); // incumbent Good, decision model Wrong
        Assert.Equal("need-more-data", c.Recommendation);
        Assert.Equal(1, report.Labels.Disagreements);
        Assert.Equal(2, report.Arms.Count);
    }

    [Fact]
    public void Report_recommends_switching_when_every_check_passes()
    {
        var rows = new List<TrialRow>();
        var labels = new List<TrialLabel>();
        for (var i = 0; i < 50; i++)
        {
            var wrong = i < 25;
            var verdict = wrong ? SongQualityVerdict.Wrong : SongQualityVerdict.Good;
            rows.Add(Row(i, GraderArm.Incumbent, Chat, verdict, latency: 10_000 + i, cost: 0.002m));
            rows.Add(Row(i, GraderArm.Decision, Jev, verdict, latency: 150 + i, cost: 0.0003m, flag: wrong ? 0.9 : 0.1, stateBytes: 9_000));
            labels.Add(new TrialLabel(i, $"f{i}", wrong ? GraderTrialLabelValue.Wrong : GraderTrialLabelValue.Right,
                UnsupportedIdentity: wrong && i < 6, GraderTrialStratum.Agreement));
        }

        var report = GraderTrialReport.Build(GraderSubjectKind.Song, rows, labels, Jev, 32_000, T0);

        var c = Assert.Single(report.Comparisons);
        Assert.All(c.Checks, check => Assert.Equal("pass", check.Status));
        Assert.Equal("switch", c.Recommendation);
        Assert.Equal(1.0, c.Kappa!.Value, 10);
        Assert.Equal(1.0, c.Challenger.Recall);
    }

    [Fact]
    public void Report_keeps_the_incumbent_when_the_challenger_misses_wrong_songs()
    {
        var rows = new List<TrialRow>();
        var labels = new List<TrialLabel>();
        for (var i = 0; i < 50; i++)
        {
            var wrong = i < 25;
            rows.Add(Row(i, GraderArm.Incumbent, Chat, wrong ? SongQualityVerdict.Wrong : SongQualityVerdict.Good, latency: 10_000, cost: 0.002m));
            // The challenger flags only the first 10 of the 25 wrong songs.
            rows.Add(Row(i, GraderArm.Decision, Jev, i < 10 ? SongQualityVerdict.Wrong : SongQualityVerdict.Good, latency: 150, cost: 0.0003m));
            labels.Add(new TrialLabel(i, $"f{i}", wrong ? GraderTrialLabelValue.Wrong : GraderTrialLabelValue.Right, false,
                wrong && i >= 10 ? GraderTrialStratum.Disagreement : GraderTrialStratum.Agreement));
        }

        var report = GraderTrialReport.Build(GraderSubjectKind.Song, rows, labels, Jev, 32_000, T0);

        var c = Assert.Single(report.Comparisons);
        Assert.Equal("fail", c.Checks.Single(x => x.Name == "quality").Status);
        Assert.Equal("keep-incumbent", c.Recommendation);
    }

    private static int[][] Matrix() => Enumerable.Range(0, 5).Select(_ => new int[5]).ToArray();

    private static TrialRow Row(
        int id, GraderArm arm, string model, SongQualityVerdict? verdict,
        GraderRunSource source = GraderRunSource.Shadow, DateTime? at = null, string? error = null,
        int? latency = 100, decimal? cost = null, double? flag = null, int? stateBytes = null) =>
        new(id, $"f{id}", arm, model,
            arm == GraderArm.Incumbent && source == GraderRunSource.Shadow ? GraderRunSource.Live : source,
            verdict, verdict is null ? null : 80, flag, null, latency, 1, 6000, 70, cost, error,
            stateBytes, stateBytes, false, at ?? T0);
}
