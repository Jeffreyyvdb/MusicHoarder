using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Options;

/// <summary>
/// Configuration for the AI library-quality grader. The grader calls any OpenAI-compatible
/// chat-completions endpoint (OpenRouter by default, but equally OpenAI, Groq, or a local Ollama)
/// so the cheap model can be swapped per-environment. Like the other providers it degrades
/// gracefully: with no <see cref="ApiKey"/> the grader is simply off and manual/auto grading
/// is a no-op rather than an error.
/// </summary>
public class QualityGradingOptions
{
    public const string SectionName = "QualityGrading";

    /// <summary>Master switch. Off unless an API key is configured (see <see cref="IsConfigured"/>).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When true, a background sweep grades enriched-but-ungraded (or stale) songs automatically,
    /// like the enrichment backfill. Manual "grade now" buttons work regardless of this flag.
    /// </summary>
    public bool AutoGradeAfterEnrichment { get; set; } = true;

    /// <summary>
    /// When true, a background sweep grades reconciled-but-ungraded (or stale) albums automatically —
    /// judging whether each canonical album is the correct match for the local files. Manual
    /// "grade album" works regardless. Shares all the LLM settings below with song grading.
    /// </summary>
    public bool AutoGradeAlbums { get; set; } = true;

    /// <summary>OpenAI-compatible base URL (must include the version path, no trailing slash needed).</summary>
    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";

    /// <summary>API key sent as <c>Authorization: Bearer</c>. Empty disables grading. Always from env/secret.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Model id, in the provider's namespace (e.g. <c>openai/gpt-4o-mini</c>, <c>google/gemini-2.0-flash-001</c>).</summary>
    public string Model { get; set; } = "openai/gpt-4o-mini";

    /// <summary>Optional OpenRouter attribution header (HTTP-Referer). Ignored when blank.</summary>
    public string Referer { get; set; } = "https://github.com/Jeffreyyvdb/MusicHoarder";

    /// <summary>Optional OpenRouter attribution header (X-Title). Ignored when blank.</summary>
    public string AppTitle { get; set; } = "MusicHoarder";

    [Range(1, 16)]
    public int Concurrency { get; set; } = 2;

    [Range(1, 50)]
    public int RequestsPerSecond { get; set; } = 4;

    [Range(5, 300)]
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Cap on the chat-completion output. Reasoning models spend this same budget on their
    /// chain-of-thought, so it must comfortably exceed <see cref="ReasoningMaxTokens"/> or the JSON
    /// answer gets truncated (the grader then fails with "No JSON object found in model reply").
    /// </summary>
    [Range(64, 16384)]
    public int MaxOutputTokens { get; set; } = 4096;

    /// <summary>
    /// Caps reasoning-model thinking via OpenRouter's <c>reasoning.max_tokens</c>, keeping the
    /// chain-of-thought from eating the whole <see cref="MaxOutputTokens"/> budget so the JSON answer
    /// always has room. Keep it well below <see cref="MaxOutputTokens"/>. <c>0</c> omits the field
    /// entirely (no reasoning control sent) for plain non-reasoning models.
    /// </summary>
    [Range(0, 8192)]
    public int ReasoningMaxTokens { get; set; } = 2000;

    /// <summary>
    /// Maximum retries for a single grading call on a transient failure (HTTP 429/5xx, a per-call
    /// timeout, or a dropped connection). <c>Retry-After</c> is honored when present; otherwise an
    /// exponential backoff with jitter is used. <c>0</c> disables retries.
    /// </summary>
    [Range(0, 6)]
    public int MaxRetries { get; set; } = 3;

    [Range(0.0, 2.0)]
    public double Temperature { get; set; } = 0.0;

    /// <summary>Bumped when the grading prompt changes so grades stay comparable across prompt iterations.</summary>
    [Range(1, 100000)]
    public int PromptVersion { get; set; } = 1;

    /// <summary>Number of songs pulled into the auto-grade channel per sweep.</summary>
    [Range(1, 10000)]
    public int BatchSize { get; set; } = 200;

    /// <summary>Delay before re-sweeping when nothing is pending auto-grade.</summary>
    [Range(5, 3600)]
    public int IdleDelaySeconds { get; set; } = 30;

    /// <summary>
    /// How long the auto-sweep skips a song after it failed to grade. A failure writes no grade row,
    /// so without this the sweep treats the song as "never graded" and re-enqueues it every
    /// <see cref="IdleDelaySeconds"/> — flooding logs and burning API credits on a reply that keeps
    /// failing. In-memory only (a restart retries), and manual "grade now" bypasses it.
    /// </summary>
    [Range(30, 86400)]
    public int FailureBackoffSeconds { get; set; } = 1800;

    /// <summary>True when a key is present and grading is enabled.</summary>
    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(BaseUrl);
}
