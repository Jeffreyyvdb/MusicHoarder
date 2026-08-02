using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Enrichment;

/// <summary>The generated pronunciation + translation documents, ready to store on the song.</summary>
public record LyricsTranslationResult(
    string? RomanizedSynced,
    string? RomanizedPlain,
    string? TranslatedSynced,
    string? TranslatedPlain,
    string? LanguageCode,
    string Model);

public interface ILyricsTranslationService
{
    bool IsConfigured { get; }

    /// <summary>
    /// Generates a per-line pronunciation guide (romanization) and English translation of the given
    /// lyrics via the configured LLM. Prefers the synced (LRC) input so timestamps can be preserved;
    /// falls back to plain lyrics (untimed output). Throws on any failure — the caller records the error.
    /// </summary>
    Task<LyricsTranslationResult> TranslateAsync(
        string? syncedLyrics, string? plainLyrics, string? artist, string? title, CancellationToken ct);
}

/// <summary>
/// On-demand lyrics pronunciation + translation over the OpenRouter endpoint/key configured under
/// <see cref="QualityGradingOptions"/> (BaseUrl + ApiKey) with its own cheap multilingual model
/// (<see cref="LyricsTranslationOptions.Model"/>) and reasoning off — same credential pattern as
/// <see cref="LlmLyricsAligner"/>. The LLM only ever sees numbered text lines (timestamps stripped);
/// code re-attaches the original LRC tags via <see cref="TranslatedLyricsAssembler"/>, so timing can
/// never be corrupted by the model. Every chunk must return exactly one entry per input line or the
/// whole generation fails — a misaligned document is worse than none.
/// </summary>
public sealed class LyricsTranslationService(
    HttpClient httpClient,
    IOptionsMonitor<QualityGradingOptions> openRouter,
    IOptionsMonitor<LyricsTranslationOptions> options,
    ILogger<LyricsTranslationService> logger) : ILyricsTranslationService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public bool IsConfigured
    {
        get
        {
            var creds = openRouter.CurrentValue;
            var opts = options.CurrentValue;
            return opts.Enabled
                && !string.IsNullOrWhiteSpace(creds.BaseUrl)
                && !string.IsNullOrWhiteSpace(creds.ApiKey)
                && !string.IsNullOrWhiteSpace(opts.Model);
        }
    }

    public async Task<LyricsTranslationResult> TranslateAsync(
        string? syncedLyrics, string? plainLyrics, string? artist, string? title, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Lyrics translation is not configured.");

        var source = !string.IsNullOrWhiteSpace(syncedLyrics)
            ? TranslatedLyricsAssembler.Parse(syncedLyrics)
            : TranslatedLyricsAssembler.Parse(plainLyrics ?? string.Empty);
        if (source.Count == 0)
            throw new InvalidOperationException("No lyric lines to translate.");

        var opts = options.CurrentValue;
        var model = opts.Model;

        string? language = null;
        var romanized = new List<string>(source.Count);
        var translated = new List<string>(source.Count);

        for (var offset = 0; offset < source.Count; offset += opts.ChunkSize)
        {
            var chunk = source.Skip(offset).Take(opts.ChunkSize).Select(l => l.Text).ToList();
            var response = await TranslateChunkAsync(chunk, artist, title, language, ct);

            language ??= response.Language?.Trim().ToLowerInvariant();

            // Whole-song-English short-circuit: signalled by an empty lines array on the first chunk.
            if (offset == 0 && language == "en" && response.Lines is not { Count: > 0 })
                return new LyricsTranslationResult(null, null, null, null, "en", model);

            if (response.Lines is null || response.Lines.Count != chunk.Count)
                throw new InvalidOperationException(
                    $"Translation LLM returned {response.Lines?.Count ?? 0} lines for a {chunk.Count}-line chunk; refusing a misaligned result.");

            for (var i = 0; i < chunk.Count; i++)
            {
                var line = response.Lines[i];
                if (line.Index != i)
                    throw new InvalidOperationException(
                        $"Translation LLM returned out-of-order line index {line.Index} (expected {i}); refusing a misaligned result.");
                romanized.Add(string.IsNullOrWhiteSpace(line.Pronunciation) ? chunk[i] : line.Pronunciation.Trim());
                translated.Add(string.IsNullOrWhiteSpace(line.Translation) ? chunk[i] : line.Translation.Trim());
            }
        }

        // A model may answer an all-English song with per-line passthrough instead of the empty-lines
        // signal; storing verbatim copies would surface a pointless toggle, so detect and drop them.
        if (language == "en"
            && romanized.SequenceEqual(source.Select(l => l.Text))
            && translated.SequenceEqual(source.Select(l => l.Text)))
        {
            return new LyricsTranslationResult(null, null, null, null, "en", model);
        }

        var (romSynced, romPlain) = TranslatedLyricsAssembler.Assemble(source, romanized);
        var (trSynced, trPlain) = TranslatedLyricsAssembler.Assemble(source, translated);
        return new LyricsTranslationResult(romSynced, romPlain, trSynced, trPlain, language, model);
    }

    private async Task<ChunkResponse> TranslateChunkAsync(
        IReadOnlyList<string> lines, string? artist, string? title, string? knownLanguage, CancellationToken ct)
    {
        const string system =
            "You help an English speaker learn to sing songs in other languages. For EACH numbered lyric " +
            "line you return two things:\n" +
            "1. \"r\" — a pronunciation guide an English speaker can sing from, matching the source " +
            "language's convention: Arabic → Arabizi chat alphabet (Latin letters with numerals: 3 for ain, " +
            "7 for haa, 2 for hamza, 5 for khaa, ...); Mandarin → Hanyu Pinyin with tone marks; Japanese → " +
            "Hepburn romaji; Korean → Revised Romanization; Russian/other Cyrillic → practical romanization; " +
            "languages already in Latin script (Spanish, French, Italian, ...) → an English phonetic " +
            "respelling with stressed syllables in CAPS (e.g. Spanish \"quiero\" → \"KYEH-roh\"). Keep the " +
            "line's word order and rhythm so it can be sung along.\n" +
            "2. \"t\" — a natural English translation of the line.\n" +
            "Lines that are already English: return the line unchanged for both \"r\" and \"t\".\n" +
            "If the ENTIRE song is in English, return {\"language\":\"en\",\"lines\":[]} instead.\n" +
            "Also detect the song's dominant language as an ISO 639-1 code.\n" +
            "Return ONLY JSON: {\"language\":\"xx\",\"lines\":[{\"i\":<line number>,\"r\":\"...\",\"t\":\"...\"}]} " +
            "with exactly one entry per input line, in order, using the given line numbers.";

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(artist) || !string.IsNullOrWhiteSpace(title))
            sb.Append("Song: ").Append(artist).Append(" — ").Append(title).Append('\n');
        if (!string.IsNullOrWhiteSpace(knownLanguage))
            sb.Append("Dominant language (already detected from earlier lines): ").Append(knownLanguage).Append('\n');
        sb.Append("LYRIC LINES (number then a tab then the text):\n");
        for (var i = 0; i < lines.Count; i++)
            sb.Append(i).Append('\t').Append(lines[i]).Append('\n');

        var content = await CompleteRawAsync(system, sb.ToString(), ct);
        var json = ExtractJson(content)
            ?? throw new InvalidOperationException("Translation LLM returned no JSON payload.");

        try
        {
            return JsonSerializer.Deserialize<ChunkResponse>(json, Json)
                ?? throw new InvalidOperationException("Translation LLM returned an empty JSON payload.");
        }
        catch (JsonException)
        {
            // A truncated array can be re-closed, but the count check in the caller will still reject it —
            // salvage only helps diagnose (the parse error becomes a clearer count-mismatch error).
            var repaired = RepairTruncatedArrayJson(json)
                ?? throw new InvalidOperationException("Translation LLM returned unparseable JSON.");
            logger.LogWarning("Translation LLM JSON was truncated; recovered the complete lines.");
            return JsonSerializer.Deserialize<ChunkResponse>(repaired, Json)
                ?? throw new InvalidOperationException("Translation LLM returned an empty JSON payload.");
        }
    }

    /// <summary>POSTs a JSON chat-completion to the OpenRouter endpoint with reasoning OFF (fast path).</summary>
    private async Task<string?> CompleteRawAsync(string system, string user, CancellationToken ct)
    {
        var creds = openRouter.CurrentValue;
        var opts = options.CurrentValue;
        var url = $"{creds.BaseUrl.TrimEnd('/')}/chat/completions";

        var body = new ChatRequest(
            opts.Model,
            new[] { new ChatReqMessage("system", system), new ChatReqMessage("user", user) },
            Temperature: 0,
            MaxTokens: opts.MaxOutputTokens,
            ResponseFormat: new ResponseFormat("json_object"));

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.ApiKey);
        if (!string.IsNullOrWhiteSpace(creds.Referer))
            req.Headers.TryAddWithoutValidation("HTTP-Referer", creds.Referer);
        if (!string.IsNullOrWhiteSpace(creds.AppTitle))
            req.Headers.TryAddWithoutValidation("X-Title", creds.AppTitle);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(opts.TimeoutSeconds));

        using var resp = await httpClient.SendAsync(req, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            var errorBody = await resp.Content.ReadAsStringAsync(cts.Token);
            // Never log the Authorization header; the URL/body carry no secret.
            logger.LogWarning("Lyrics translation LLM ({Model}) failed: {Status} {Body}",
                opts.Model, (int)resp.StatusCode, Truncate(errorBody, 300));
            throw new InvalidOperationException(
                $"Translation LLM request failed with HTTP {(int)resp.StatusCode}.");
        }

        var parsed = await resp.Content.ReadFromJsonAsync<ChatResponse>(Json, cts.Token);
        return parsed?.Choices?.FirstOrDefault()?.Message?.Content;
    }

    /// <summary>Closes a <c>{"lines":[ ... ]}</c> payload truncated mid-array at the last complete object.</summary>
    private static string? RepairTruncatedArrayJson(string json)
    {
        var lastClose = json.LastIndexOf('}');
        if (lastClose <= 0)
            return null;
        var candidate = json[..(lastClose + 1)] + "]}";
        try
        {
            using var _ = JsonDocument.Parse(candidate);
            return candidate;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ExtractJson(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        return start >= 0 && end > start ? content[start..(end + 1)] : null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    // --- request/response DTOs ---

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatReqMessage> Messages,
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("response_format")] ResponseFormat ResponseFormat);

    private sealed record ChatReqMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ResponseFormat([property: JsonPropertyName("type")] string Type);

    private sealed record ChatResponse(
        [property: JsonPropertyName("choices")] List<ChatChoice>? Choices);

    private sealed record ChatChoice(
        [property: JsonPropertyName("message")] ChatRespMessage? Message);

    private sealed record ChatRespMessage(
        [property: JsonPropertyName("content")] string? Content);

    private sealed record ChunkResponse(
        [property: JsonPropertyName("language")] string? Language,
        [property: JsonPropertyName("lines")] List<ChunkLine>? Lines);

    private sealed record ChunkLine(
        [property: JsonPropertyName("i")] int Index,
        [property: JsonPropertyName("r")] string? Pronunciation,
        [property: JsonPropertyName("t")] string? Translation);
}
