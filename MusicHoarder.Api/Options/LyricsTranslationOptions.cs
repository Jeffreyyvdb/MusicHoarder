using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Options;

/// <summary>
/// Configuration for the on-demand lyrics pronunciation (romanization) + English translation feature.
/// A single LLM call per song returns, for every lyric line, a pronunciation guide for an English
/// speaker singing along (Arabizi for Arabic, pinyin for Mandarin, romaji for Japanese, phonetic
/// respelling for Latin-script languages) plus a natural English translation. Results are display-only —
/// they are never embedded into destination files.
///
/// No credentials live here: the call goes over the same OpenRouter endpoint/key configured under
/// <c>QualityGrading</c> (BaseUrl + ApiKey), exactly like <c>LyricsTranscription:LlmModel</c> does.
/// With those creds absent the feature is simply off and the translate endpoint returns 503.
/// </summary>
public class LyricsTranslationOptions
{
    public const string SectionName = "LyricsTranslation";

    /// <summary>Master switch. Even when true the feature needs the QualityGrading OpenRouter creds.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Model id for the translation LLM. Needs strong multilingual coverage but no reasoning — a cheap
    /// Flash-class model is ideal, and deliberately separate from the <c>QualityGrading:Model</c> grader.
    /// </summary>
    public string Model { get; set; } = "google/gemini-2.5-flash";

    /// <summary>Per-chunk LLM call timeout.</summary>
    [Range(10, 300)]
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Lyric lines per LLM call. Long songs are chunked so per-line output stays well inside the model's
    /// token budget; each chunk is validated to return exactly one entry per input line.
    /// </summary>
    [Range(10, 200)]
    public int ChunkSize { get; set; } = 60;

    /// <summary>Output token budget per chunk (each line yields a pronunciation + a translation).</summary>
    [Range(1024, 65536)]
    public int MaxOutputTokens { get; set; } = 16384;
}
