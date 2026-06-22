using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Options;

/// <summary>
/// Configuration for the experimental AI lyrics transcription feature. It POSTs a song's audio to an
/// OpenAI-compatible <c>/audio/transcriptions</c> endpoint (OpenAI Whisper by default) and builds a
/// synced LRC + plain transcript from the returned <c>segments</c>/<c>words</c>. Like the other
/// providers it degrades gracefully: with no <see cref="ApiKey"/> the feature is simply off and the
/// transcribe endpoint returns 503 rather than erroring the app.
///
/// Important: only OpenAI's <c>whisper-1</c> returns word/segment timestamps via
/// <c>response_format=verbose_json</c> — the newer <c>gpt-4o-transcribe</c> models reject it, and
/// OpenRouter's STT proxy strips the timestamps entirely. Keep <see cref="Model"/> on a
/// timestamp-returning model (or repoint <see cref="BaseUrl"/> at Groq / a self-hosted whisper).
/// </summary>
public class LyricsTranscriptionOptions
{
    public const string SectionName = "LyricsTranscription";

    /// <summary>Master switch. Off unless an API key is configured (see <see cref="IsConfigured"/>).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>OpenAI-compatible base URL (no trailing slash needed); <c>/audio/transcriptions</c> is appended.</summary>
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>API key sent as <c>Authorization: Bearer</c>. Empty disables transcription. Always from env/secret.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Transcription model id. Must support verbose_json timestamps (e.g. <c>whisper-1</c>).</summary>
    public string Model { get; set; } = "whisper-1";

    /// <summary>Per-call timeout. Whole-song uploads + transcription can take a while on long tracks.</summary>
    [Range(10, 600)]
    public int TimeoutSeconds { get; set; } = 180;

    /// <summary>Retries on a transient failure (HTTP 429/5xx, timeout, dropped connection). <c>0</c> disables.</summary>
    [Range(0, 6)]
    public int MaxRetries { get; set; } = 2;

    /// <summary>
    /// LRC line-splitting: Whisper emits one timestamp per coarse <em>segment</em> (often several sung
    /// lines merged). We re-chunk using the word-level timestamps, breaking a line whenever the silent
    /// gap between two consecutive words reaches this many seconds — approximating LRCLIB's per-line
    /// granularity. Lower = more, shorter lines. <c>0</c> disables gap-based splitting (cap-only).
    /// </summary>
    [Range(0.0, 5.0)]
    public double LineSplitPauseSeconds { get; set; } = 0.6;

    /// <summary>Hard cap on words per LRC line, so a long pause-less run still breaks into readable lines.</summary>
    [Range(1, 40)]
    public int LineSplitMaxWords { get; set; } = 7;

    /// <summary>
    /// When true (and an LLM is configured), refine the LRC with an LLM: align official lyric lines
    /// (LRCLIB plain) to Whisper's word clock, or — when no lyrics text exists — re-segment the transcript
    /// into natural, punctuated lines. The LLM only chooses word boundaries; timing always comes from
    /// Whisper. Falls back to the pause/word-cap heuristic when off, unconfigured, or it fails.
    /// </summary>
    public bool UseLlmAlignment { get; set; } = true;

    /// <summary>
    /// Model id for the alignment/re-segmentation LLM. This is a fast, mechanical text-mapping task, so a
    /// cheap low-latency non-reasoning model is ideal (default: Gemini 2.5 Flash Lite) — deliberately
    /// separate from the heavier <c>QualityGrading:Model</c> reasoning model. It is called over the same
    /// OpenRouter endpoint/key configured under <c>QualityGrading</c> (BaseUrl + ApiKey), with reasoning
    /// off, so no extra credentials are needed.
    /// </summary>
    public string LlmModel { get; set; } = "google/gemini-2.5-flash-lite";

    /// <summary>True when a key is present and the feature is enabled.</summary>
    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(BaseUrl);
}
