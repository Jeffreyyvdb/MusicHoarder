using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

/// <summary>What a trial row grades. The numbers are a persisted contract.</summary>
public enum GraderSubjectKind
{
    Song = 0,
    Album = 1,
}

/// <summary>Which grader produced a trial row. The numbers are a persisted contract.</summary>
public enum GraderArm
{
    /// <summary>The chat-completions grader whose grades are authoritative (<see cref="SongQualityGrade"/>).</summary>
    Incumbent = 0,
    /// <summary>A model on OpenRouter's Decisions API (Jev, or another decision model), in shadow.</summary>
    Decision = 1,
}

/// <summary>Why a trial row exists. The numbers are a persisted contract.</summary>
public enum GraderRunSource
{
    /// <summary>The incumbent's real grade, recorded as it was written.</summary>
    Live = 0,
    /// <summary>The incumbent's grade, copied from a grade written before the trial recorded it (no cost or attempts).</summary>
    Backfill = 1,
    /// <summary>A decision model's grade from the shadow sweep.</summary>
    Shadow = 2,
    /// <summary>A decision model's grade from the admin "run now" endpoint.</summary>
    Manual = 3,
    /// <summary>A repeat of a grade on the same input, to measure how often the verdict flips.</summary>
    Stability = 4,
}

/// <summary>
/// One grader's answer for one subject in the grader trial — the chat grader and each decision
/// model, side by side on the same dossier (<see cref="InputFingerprint"/>). Lives apart from
/// <see cref="SongQualityGrade"/> on purpose: a shadow row there would become the song's current
/// grade (the newest row wins) and move it in or out of the Inbox mid-trial.
/// </summary>
public class GraderComparisonRun
{
    [Key]
    public long Id { get; set; }

    public GraderSubjectKind SubjectKind { get; set; }

    /// <summary>The song id or canonical album id. Not a foreign key: one column serves both kinds.</summary>
    public int SubjectId { get; set; }

    /// <summary>Owner of the graded subject, for the tenancy filter.</summary>
    public Guid OwnerUserId { get; set; }

    public GraderArm Arm { get; set; }

    public GraderRunSource Source { get; set; }

    [MaxLength(128)]
    public string Model { get; set; } = "";

    /// <summary>The incumbent's prompt version, or the decision model's question-set version.</summary>
    public int Version { get; set; }

    /// <summary>Hash of the full dossier — the same hash the authoritative grade stores — so arms pair on identical input.</summary>
    [MaxLength(64)]
    public string InputFingerprint { get; set; } = "";

    /// <summary>UTF-8 bytes of the state as built, before any shedding (decision arms only).</summary>
    public int? FullStateBytes { get; set; }

    /// <summary>UTF-8 bytes of the state actually sent (decision arms only).</summary>
    public int? StateBytes { get; set; }

    /// <summary>True when the change log was shed to bring the state under the size limit.</summary>
    public bool StateShed { get; set; }

    /// <summary>Null when the call failed (see <see cref="ErrorCode"/>).</summary>
    public SongQualityVerdict? Verdict { get; set; }

    public int? Score { get; set; }

    /// <summary>The decision model's probability of "wrong or questionable". Null for the incumbent, which gives none.</summary>
    public double? FlagProbability { get; set; }

    /// <summary>
    /// Probability of an unsupported identity (songs only). The decision model's answer, or 1/0 for
    /// whether the incumbent raised the <c>unsupported_identity</c> issue.
    /// </summary>
    public double? UnsupportedIdentity { get; set; }

    /// <summary>JSON array of issue codes the arm raised.</summary>
    public string? IssueCodesJson { get; set; }

    /// <summary>The decision model's answers with their probabilities, as JSON.</summary>
    public string? ProbabilitiesJson { get; set; }

    [MaxLength(1024)]
    public string? Summary { get; set; }

    /// <summary>Wall clock for the whole grade, retries included.</summary>
    public int? LatencyMs { get; set; }

    /// <summary>Requests made for this grade (the incumbent asks again when a reply holds no grade).</summary>
    public int? Attempts { get; set; }

    public int? InputTokens { get; set; }

    public int? OutputTokens { get; set; }

    /// <summary>USD the provider billed for this grade (<c>usage.cost</c>), retries included. Null when not reported.</summary>
    public decimal? CostUsd { get; set; }

    /// <summary>Set when the grade failed: the same codes the grading run records (<c>http_error</c>, <c>bad_response</c>…).</summary>
    [MaxLength(32)]
    public string? ErrorCode { get; set; }

    [MaxLength(512)]
    public string? Error { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>A person's answer to "is this subject's metadata right?", the truth the trial measures quality against.</summary>
public enum GraderTrialLabelValue
{
    Right = 1,
    Wrong = 2,
    Unsure = 3,
}

/// <summary>
/// Which pool a labelled subject was drawn from. Disagreements are oversampled, so each stratum is
/// weighted back by its share of all pairs when precision and recall are calculated.
/// </summary>
public enum GraderTrialStratum
{
    Disagreement = 0,
    Agreement = 1,
}

/// <summary>One human label in the grader trial, for one subject on one dossier.</summary>
public class GraderTrialLabel
{
    [Key]
    public int Id { get; set; }

    public GraderSubjectKind SubjectKind { get; set; }

    public int SubjectId { get; set; }

    public Guid OwnerUserId { get; set; }

    [MaxLength(64)]
    public string InputFingerprint { get; set; } = "";

    public GraderTrialLabelValue Label { get; set; }

    /// <summary>
    /// Ticked with <see cref="GraderTrialLabelValue.Wrong"/> when the subject's identity is not
    /// supported by any source — the failure the grader exists to catch.
    /// </summary>
    public bool UnsupportedIdentity { get; set; }

    public GraderTrialStratum Stratum { get; set; }

    public DateTime LabeledAtUtc { get; set; }
}
