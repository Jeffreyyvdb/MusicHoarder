using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Options;

/// <summary>
/// The grader trial: TypeSafe's Jev decision model (and any other model on OpenRouter's Decisions
/// API) grades the same dossiers as the chat grader, in shadow, so the two can be compared on speed,
/// price and quality. Shadow grades go to <c>GraderComparisonRuns</c> only — never to
/// <c>SongQualityGrades</c> — so the trial cannot change a current verdict, the Inbox or a rollup.
/// <para>
/// It reuses <see cref="QualityGradingOptions.ApiKey"/> (the same OpenRouter key), so it needs no new
/// secret, and it is off whenever quality grading is off.
/// </para>
/// </summary>
public class JevGradingOptions
{
    public const string SectionName = "JevGrading";

    /// <summary>Master switch for the trial: the shadow sweep and the incumbent's comparison rows.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Base URL of the Decisions API (the request goes to <c>{BaseUrl}/decisions</c>).</summary>
    public string BaseUrl { get; set; } = "https://openrouter.ai/api/alpha";

    /// <summary>
    /// Comma-separated decision model ids, each one its own trial arm. Pin a dated id so results stay
    /// comparable: <c>~typesafe/jev-latest</c> follows new releases without notice. A string rather
    /// than a list because the configuration binder appends to a list's defaults instead of replacing them.
    /// </summary>
    public string Models { get; set; } = "typesafe/jev-1.14-20261014";

    /// <summary>Share of graded subjects the shadow sweep also grades with each decision model (0–1), picked by id so the sample is stable.</summary>
    [Range(0.0, 1.0)]
    public double ShadowSampleRate { get; set; } = 1.0;

    /// <summary>When true, the shadow sweep also grades albums, not only songs.</summary>
    public bool GradeAlbums { get; set; } = true;

    [Range(1, 16)]
    public int Concurrency { get; set; } = 2;

    [Range(1, 50)]
    public int RequestsPerSecond { get; set; } = 4;

    [Range(5, 300)]
    public int TimeoutSeconds { get; set; } = 30;

    [Range(0, 6)]
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Ceiling on the serialized state, in UTF-8 bytes. Jev runs a request with more than about
    /// 32,000 bytes of text in its long-context mode, which TypeSafe says is slower and less
    /// accurate, so the change log is shed until the state fits. A state that still does not fit is
    /// sent anyway and recorded as over the limit.
    /// </summary>
    [Range(2000, 1_000_000)]
    public int MaxStateBytes { get; set; } = 32_000;

    /// <summary>Subjects the shadow sweep grades per pass.</summary>
    [Range(1, 2000)]
    public int BatchSize { get; set; } = 100;

    /// <summary>Delay before the next sweep when nothing was graded.</summary>
    [Range(5, 3600)]
    public int IdleDelaySeconds { get; set; } = 60;

    /// <summary>How long the sweep leaves a subject alone after its decision call failed, or its dossier no longer matched its grade.</summary>
    [Range(30, 86400)]
    public int FailureBackoffSeconds { get; set; } = 1800;

    /// <summary>The configured model ids, in order. The first is the primary arm (the one labels are drawn for).</summary>
    public IReadOnlyList<string> ModelList =>
        Models.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>The model the labelling queue and the decision rule look at first.</summary>
    public string? PrimaryModel => ModelList.FirstOrDefault();
}
