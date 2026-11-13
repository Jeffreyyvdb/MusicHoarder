using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Quality.Jev;

/// <summary>
/// What a decision model's answers mean as a grade. <see cref="FlagProbability"/> is the model's
/// probability that the subject belongs in the AI-flagged queue (wrong or questionable): the chat
/// grader has no such number, which is half of why the trial exists.
/// </summary>
public sealed record JevGrade(
    SongQualityVerdict Verdict,
    int Score,
    string Summary,
    IReadOnlyList<GradingIssue> Issues,
    double? FlagProbability,
    double? UnsupportedIdentity,
    string ProbabilitiesJson);

/// <summary>A state ready to send, and how much shedding it took to get there.</summary>
public sealed record JevState(JsonNode State, int FullBytes, int SentBytes, bool Shed);

/// <summary>
/// The grading rubric of <see cref="QualityGradingPrompt"/> and <see cref="AlbumGradingPrompt"/>,
/// rewritten as typed questions. The rules every question needs (what is ground truth, the
/// unreleased-track exemption) go once into the state as <c>audit</c>, so each question's own text
/// stays short; questions in one request run in parallel and do not see each other's answers.
/// Bump a set's version whenever its wording changes, so stored answers stay comparable.
/// </summary>
public static class JevQuestions
{
    public const int SongVersion = 1;
    public const int AlbumVersion = 1;

    /// <summary>A noul answer at or above this probability raises the matching issue code.</summary>
    public const double IssueThreshold = 0.5;

    /// <summary>A raised issue at or above this probability is high severity.</summary>
    public const double HighSeverityThreshold = 0.8;

    private static readonly JsonSerializerOptions StateJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions CompactJson = new(JsonSerializerDefaults.Web);

    // Verdict keys, in the order of SongQualityVerdict's bands.
    private const string Excellent = "excellent", Good = "good", Questionable = "questionable", Wrong = "wrong", Ungradeable = "ungradeable";

    // Score levels map back to the chat grader's 0–100 bands through the middle of each band.
    private static readonly double[] LevelScores = [20, 55, 80, 95];

    private const string SongAudit =
        """
        You audit one song in a music-library pipeline. The pipeline scanned an audio file, asked several
        metadata providers (AcoustID, MusicBrainz, Deezer, Spotify, Apple Music, community trackers), then
        chose a set of tags and a destination path. Judge whether that choice is correct; do not identify
        the song yourself.
        """;

    private static readonly string[] SongRules =
    [
        "dossier.currentMetadata and dossier.destinationPathPreview are what the pipeline chose and will write. Judge these.",
        "A changeLog entry with proposed=true and applied=false was NOT applied: the pipeline declined to overwrite the tag. Never count it as a change, and never as a mismatch.",
        "When dossier.enrichment.isUnreleased is true, or the match came from a community tracker, mainstream providers cannot corroborate the song by definition. Do not treat that as wrong; judge internal consistency instead. A leak named with a working title that differs from the tracker's title is normal.",
        "The failure this audit exists to catch: every provider returned no match, yet the final metadata and path still name a specific, unrelated song, artist or album.",
    ];

    private const string AlbumAudit =
        """
        You audit one album link in a music-library pipeline. The pipeline grouped the user's local files
        into an album, searched several providers for it, and reconciled their tracklists into one
        canonical album. Judge whether the canonical album is the correct album for the local files; do
        not identify the album yourself.
        """;

    private static readonly string[] AlbumRules =
    [
        "dossier.localAlbum is ground truth: the songs the user actually has.",
        "dossier.matchSummary.ownedUnmatchedCount counts owned songs that map to no canonical track. A high count relative to ownedCount is the clearest sign of a wrong album.",
        "dossier.songGradeRollup counts the owned songs' own grades. Many wrong songs support a wrong album link.",
        "A deluxe or standard edition with a few extra or missing tracks is still the right album when the owned songs line up.",
    ];

    // ---------------------------------------------------------------- songs

    public static IReadOnlyDictionary<string, DecisionQuestion> SongQuestions { get; } = new Dictionary<string, DecisionQuestion>
    {
        ["verdict"] = new(DecisionQuestionType.Choice,
            "How trustworthy are the final chosen metadata and destination path of this song, given the evidence in the dossier?",
            [
                new(Excellent, "Correct and corroborated by two or more independent providers or a strong ID (matching ISRC or MBID); artist, title, album and year present; the path matches the metadata."),
                new(Good, "Correct and consistent, but thinly sourced or missing minor fields."),
                new(Questionable, "Plausible but unverified, internally inconsistent, or one low-confidence source; a human should check."),
                new(Wrong, "The chosen metadata or path contradicts the file's own tags or filename, or no provider matched yet the song was given a confident identity."),
                new(Ungradeable, "The dossier holds no information to judge."),
            ]),
        ["quality"] = new(DecisionQuestionType.Score,
            "Rate the quality of the final chosen metadata and destination path.",
            [
                new("0", "Wrong: contradicts the source file, or an identity no provider supports"),
                new("1", "Questionable: plausible but unverified or inconsistent"),
                new("2", "Good: correct and consistent, thinly sourced or minor fields missing"),
                new("3", "Excellent: correct, corroborated and complete"),
            ]),
        ["unsupported_identity"] = new(DecisionQuestionType.Noul,
            "Did the song get a specific artist, title or album that no provider attempt supports?",
            [
                new("true", "Every provider returned no match or a different song, yet the final metadata names a specific song, artist or album."),
                new("false", "At least one provider, a strong ID, or the file's own tags support the final identity."),
            ]),
        ["path_metadata_mismatch"] = new(DecisionQuestionType.Noul,
            "Does the destination path disagree with the final chosen metadata?",
            [
                new("true", "The artist, album or title in destinationPathPreview differs from currentMetadata."),
                new("false", "The path matches the metadata, or there is no path."),
            ]),
        ["identity_changed"] = new(DecisionQuestionType.Choice,
            "Which part of the song's identity did the pipeline change from the file's embedded tags, if any? Count only applied changes.",
            [
                new("none", "Artist, title and album are the same as the embedded tags, apart from spelling, case or punctuation."),
                new("artist", "The artist became a different artist."),
                new("title", "The title became a different song title."),
                new("album", "The album became a different album."),
            ]),
        ["single_source"] = new(DecisionQuestionType.Noul,
            "Does the final identity rest on one provider alone?",
            [
                new("true", "Exactly one provider supports the identity and nothing else corroborates it. Unreleased or community-tracker songs do not count."),
                new("false", "Two or more providers or a strong ID agree, or the song is unreleased or from a community tracker."),
            ]),
    };

    public static JevState SongState(SongGradingDossier dossier, int maxBytes)
    {
        var fullBytes = Bytes(Envelope(SongAudit, SongRules, dossier));
        if (fullBytes <= maxBytes)
            return new JevState(Envelope(SongAudit, SongRules, dossier), fullBytes, fullBytes, Shed: false);

        // Shed the change log (oldest first), halving until the state fits; everything else is fixed-size.
        var total = dossier.ChangeLog.Count;
        var originalTotal = dossier.Truncation?.ChangeLogTotal ?? total;
        var keep = total / 2;
        while (true)
        {
            var kept = dossier.ChangeLog.Skip(total - keep).ToList();
            var shed = dossier with
            {
                ChangeLog = kept,
                Truncation = new DossierTruncation(originalTotal, keep,
                    $"changeLog holds the {keep} most recent of {originalTotal} entries; older entries were omitted to fit"),
            };
            var state = Envelope(SongAudit, SongRules, shed);
            var bytes = Bytes(state);
            if (bytes <= maxBytes || keep == 0)
                return new JevState(state, fullBytes, bytes, Shed: true);
            keep /= 2;
        }
    }

    public static JevGrade MapSong(IReadOnlyDictionary<string, DecisionAnswer> answers)
    {
        var (verdict, flag) = ReadVerdict(answers);
        var score = ReadScore(answers, verdict);

        var issues = new List<GradingIssue>();
        var unsupported = Noul(answers, "unsupported_identity");
        AddNoulIssue(issues, "unsupported_identity", unsupported);
        AddNoulIssue(issues, "path_metadata_mismatch", Noul(answers, "path_metadata_mismatch"));
        AddNoulIssue(issues, "single_source", Noul(answers, "single_source"));
        if (answers.TryGetValue("identity_changed", out var changed)
            && changed.Choice is { } part && part != "none"
            && Probability(changed, part) is { } p && p >= IssueThreshold)
        {
            issues.Add(new GradingIssue($"{part}_changed", Severity(p), Detail(p)));
        }

        return new JevGrade(verdict, score, Summarize(verdict, answers, issues), issues, flag, unsupported, ProbabilitiesJson(answers));
    }

    // ---------------------------------------------------------------- albums

    public static IReadOnlyDictionary<string, DecisionQuestion> AlbumQuestions { get; } = new Dictionary<string, DecisionQuestion>
    {
        ["verdict"] = new(DecisionQuestionType.Choice,
            "Is the canonical album the correct album for the user's local files?",
            [
                new(Excellent, "Clearly the same album: the owned songs line up with the canonical tracklist, two or more providers agree, and the track count matches."),
                new(Good, "The correct album with minor edition differences (a deluxe or standard gap, a few bonus tracks)."),
                new(Questionable, "Plausibly right but thinly sourced, providers disagree on its length, or a meaningful share of owned songs do not line up."),
                new(Wrong, "A different album: most owned songs map to no canonical track, the artist or title is another album, or the songs' own grades say they are mis-tagged."),
                new(Ungradeable, "There are no owned songs or no canonical tracks to compare."),
            ]),
        ["quality"] = new(DecisionQuestionType.Score,
            "Rate how well the canonical album matches the local files.",
            [
                new("0", "Wrong: a different album"),
                new("1", "Questionable: plausible but unverified"),
                new("2", "Good: the right album, minor edition differences"),
                new("3", "Excellent: clearly the same album, corroborated"),
            ]),
        ["wrong_album"] = new(DecisionQuestionType.Noul,
            "Is the canonical album a different album from the one the local files belong to?",
            [
                new("true", "A different album, or another artist's album."),
                new("false", "The same album, in any edition."),
            ]),
        ["wrong_edition"] = new(DecisionQuestionType.Noul,
            "Is the canonical album the right album but a different edition from the local files?",
            [
                new("true", "Same album, but the tracklist is a different edition (deluxe, standard, regional) than the files suggest."),
                new("false", "The edition fits, or it is a different album altogether."),
            ]),
        ["owned_tracks_absent"] = new(DecisionQuestionType.Noul,
            "Are a meaningful share of the owned songs absent from the canonical tracklist?",
            [
                new("true", "Several owned songs map to no canonical track."),
                new("false", "Nearly every owned song maps to a canonical track."),
            ]),
    };

    public static JevState AlbumState(AlbumGradingDossier dossier)
    {
        var state = Envelope(AlbumAudit, AlbumRules, dossier);
        var bytes = Bytes(state);
        return new JevState(state, bytes, bytes, Shed: false);
    }

    public static JevGrade MapAlbum(IReadOnlyDictionary<string, DecisionAnswer> answers)
    {
        var (verdict, flag) = ReadVerdict(answers);
        var score = ReadScore(answers, verdict);
        var issues = new List<GradingIssue>();
        AddNoulIssue(issues, "wrong_album", Noul(answers, "wrong_album"));
        AddNoulIssue(issues, "wrong_edition", Noul(answers, "wrong_edition"));
        AddNoulIssue(issues, "owned_tracks_absent", Noul(answers, "owned_tracks_absent"));
        return new JevGrade(verdict, score, Summarize(verdict, answers, issues), issues, flag, null, ProbabilitiesJson(answers));
    }

    // ---------------------------------------------------------------- shared

    /// <summary>
    /// Maps a 0-based score position on the four levels to 0–100 by interpolating between the middle
    /// of each of the chat grader's bands (wrong 20, questionable 55, good 80, excellent 95).
    /// </summary>
    public static int ScoreFromPosition(double position)
    {
        var p = Math.Clamp(position, 0, LevelScores.Length - 1);
        var i = (int)Math.Floor(p);
        if (i >= LevelScores.Length - 1) return (int)LevelScores[^1];
        var value = LevelScores[i] + (p - i) * (LevelScores[i + 1] - LevelScores[i]);
        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    private static (SongQualityVerdict Verdict, double? Flag) ReadVerdict(IReadOnlyDictionary<string, DecisionAnswer> answers)
    {
        if (!answers.TryGetValue("verdict", out var v) || v.Choice is null)
            throw new JsonException("Decisions response held no verdict.");

        var verdict = v.Choice switch
        {
            Excellent => SongQualityVerdict.Excellent,
            Good => SongQualityVerdict.Good,
            Questionable => SongQualityVerdict.Questionable,
            Wrong => SongQualityVerdict.Wrong,
            Ungradeable => SongQualityVerdict.Ungradeable,
            _ => throw new JsonException($"Decisions response named an unknown verdict '{v.Choice}'."),
        };

        double? flag = v.Probabilities is { } probs
            ? probs.GetValueOrDefault(Wrong) + probs.GetValueOrDefault(Questionable)
            : null;
        return (verdict, flag is null ? null : Math.Clamp(flag.Value, 0, 1));
    }

    private static int ReadScore(IReadOnlyDictionary<string, DecisionAnswer> answers, SongQualityVerdict verdict)
    {
        if (verdict == SongQualityVerdict.Ungradeable) return 0;
        if (answers.TryGetValue("quality", out var q) && q.Score is { } position)
            return ScoreFromPosition(position);
        // No score answer: the middle of the verdict's band, as the chat grader's parser falls back the other way.
        return verdict switch
        {
            SongQualityVerdict.Excellent => 95,
            SongQualityVerdict.Good => 80,
            SongQualityVerdict.Questionable => 55,
            _ => 20,
        };
    }

    private static double? Noul(IReadOnlyDictionary<string, DecisionAnswer> answers, string key) =>
        answers.TryGetValue(key, out var a) ? a.Noul : null;

    private static double? Probability(DecisionAnswer answer, string option) =>
        answer.Probabilities is { } p && p.TryGetValue(option, out var v) ? v : answer.Confidence;

    private static void AddNoulIssue(List<GradingIssue> issues, string code, double? p)
    {
        if (p is { } value && value >= IssueThreshold)
            issues.Add(new GradingIssue(code, Severity(value), Detail(value)));
    }

    private static string Severity(double p) => p >= HighSeverityThreshold ? "high" : "medium";

    private static string Detail(double p) => $"p={Percent(p)}";

    public static string Percent(double p) => $"{Math.Round(p * 100, MidpointRounding.AwayFromZero):0}%";

    private static string Summarize(SongQualityVerdict verdict, IReadOnlyDictionary<string, DecisionAnswer> answers, IReadOnlyList<GradingIssue> issues)
    {
        var conf = answers.TryGetValue("verdict", out var v) && v.Probabilities?.GetValueOrDefault(v.Choice ?? "") is double p and > 0
            ? $" ({Percent(p)})"
            : "";
        var summary = $"{verdict}{conf}";
        if (issues.Count > 0)
            summary += "; flags: " + string.Join(", ", issues.Select(i => $"{i.Code.Replace('_', ' ')} ({i.Detail})"));
        return summary.Length > 1024 ? summary[..1024] : summary;
    }

    private static string ProbabilitiesJson(IReadOnlyDictionary<string, DecisionAnswer> answers) =>
        JsonSerializer.Serialize(answers.ToDictionary(
            a => a.Key,
            a => a.Value.Noul is { } n ? (object)n : new { a.Value.Choice, a.Value.Score, a.Value.Probabilities }),
            CompactJson);

    private static JsonObject Envelope<T>(string task, string[] rules, T dossier) => new()
    {
        ["audit"] = new JsonObject
        {
            ["task"] = task.ReplaceLineEndings(" ").Trim(),
            ["rules"] = new JsonArray(rules.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
        },
        ["dossier"] = JsonSerializer.SerializeToNode(dossier, StateJson),
    };

    private static int Bytes(JsonNode state) => System.Text.Encoding.UTF8.GetByteCount(state.ToJsonString());
}
