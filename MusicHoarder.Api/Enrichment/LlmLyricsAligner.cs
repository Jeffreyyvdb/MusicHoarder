using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Enrichment;

/// <summary>A transcript word with its audio start/end time (seconds), from Whisper's verbose output.</summary>
public record TimedWord(string Word, double Start, double End);

/// <summary>
/// Uses a fast, cheap LLM to turn a raw word-level Whisper transcript into clean, naturally-segmented
/// karaoke lines. It calls the same OpenRouter endpoint/key configured under <see cref="QualityGradingOptions"/>
/// (BaseUrl + ApiKey) but with its OWN model (<see cref="LyricsTranscriptionOptions.LlmModel"/>, default
/// Gemini 2.5 Flash Lite) and <b>reasoning off</b> — alignment is a mechanical text-mapping task, so the
/// heavier reasoning grader model is both slower and pricier than it needs to be.
///
/// The crucial design rule: <b>the LLM never produces timestamps</b> (LLMs can't time audio). It only
/// returns <i>word indices</i> — which transcript word a line starts on — and code maps those to Whisper's
/// accurate word clock. Any failure returns <c>null</c> so the caller falls back to a deterministic split.
/// </summary>
public sealed class LlmLyricsAligner(
    HttpClient httpClient,
    IOptionsMonitor<QualityGradingOptions> openRouter,
    IOptionsMonitor<LyricsTranscriptionOptions> options,
    ILogger<LlmLyricsAligner> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>True when the OpenRouter credentials (from grading) and an alignment model are configured.</summary>
    public bool IsAvailable
    {
        get
        {
            var creds = openRouter.CurrentValue;
            return !string.IsNullOrWhiteSpace(creds.BaseUrl)
                && !string.IsNullOrWhiteSpace(creds.ApiKey)
                && !string.IsNullOrWhiteSpace(options.CurrentValue.LlmModel);
        }
    }

    /// <summary>
    /// Tier B — align known official lyric lines (correct text, breaks, punctuation, e.g. LRCLIB plain)
    /// to the word clock: the LLM maps each lyric line to its starting transcript word, code times it.
    /// Returns the lyric lines with Whisper-derived start times, or null if the mapping is unusable.
    /// </summary>
    public async Task<List<(double Start, string Text)>?> AlignReferenceLinesAsync(
        IReadOnlyList<string> referenceLines, IReadOnlyList<TimedWord> words, CancellationToken ct)
    {
        if (referenceLines.Count == 0 || words.Count == 0 || !IsAvailable)
            return null;

        const string system =
            "You align official written song lyrics to a word-level audio transcript. For each lyric line, " +
            "identify the transcript word index where that line STARTS being sung. The transcript may mishear " +
            "words, include ad-libs, or repeat sections — pick the best match and keep indices non-decreasing. " +
            "When the same line repeats, map each repetition to its OWN successive occurrence in the transcript " +
            "(do not reuse the same index). Return ONLY JSON.";
        var user =
            $"LYRIC LINES (index then a tab then the text):\n{NumberLines(referenceLines)}\n" +
            $"TRANSCRIPT WORDS (index:word):\n{WordStream(words)}\n\n" +
            "Return JSON: {\"lines\":[{\"line\":<lyric line index>,\"startWord\":<transcript word index>}, ...]} " +
            "with exactly one entry per lyric line, in order, startWord non-decreasing.";

        // Big output budget: one entry per lyric line for a potentially long song.
        var parsed = await CompleteJsonAsync<AlignResponse>(system, user, 16384, ct);
        if (parsed?.Lines is not { Count: > 0 })
            return null;

        var map = new Dictionary<int, int>();
        foreach (var l in parsed.Lines)
            if (l.Line >= 0 && l.Line < referenceLines.Count)
                map[l.Line] = Math.Clamp(l.StartWord, 0, words.Count - 1);

        // Too few lines mapped → the model lost alignment; don't trust it.
        if (map.Count < referenceLines.Count * 0.5)
            return null;

        var lines = new List<(double, string)>(referenceLines.Count);
        double lastTime = 0;
        var lastWord = 0;
        for (var i = 0; i < referenceLines.Count; i++)
        {
            var sw = map.TryGetValue(i, out var v) ? Math.Max(v, lastWord) : lastWord;
            var t = Math.Max(words[sw].Start, lastTime);
            lines.Add((t, referenceLines[i]));
            lastTime = t;
            lastWord = sw;
        }
        return lines;
    }

    /// <summary>
    /// Tier C — no reference text available: ask the LLM to re-segment the raw transcript into natural,
    /// punctuated lyric lines (returning each line's text + its starting word index), then time each line
    /// from that word. Returns null on any failure so the caller uses the heuristic split instead.
    /// </summary>
    public async Task<List<(double Start, string Text)>?> ResegmentAsync(
        IReadOnlyList<TimedWord> words, CancellationToken ct)
    {
        if (words.Count == 0 || !IsAvailable)
            return null;

        const string system =
            "You turn a raw word-level transcript of a song into natural, properly punctuated and " +
            "capitalized lyric lines, like an official lyrics sheet. Keep the words in order; do not invent " +
            "or drop content. Return ONLY JSON.";
        var user =
            $"TRANSCRIPT WORDS (index:word):\n{WordStream(words)}\n\n" +
            "Return JSON: {\"lines\":[{\"startWord\":<index of the first word of the line>," +
            "\"text\":\"<the line, cleaned and punctuated>\"}, ...]} covering the words in order, " +
            "startWord non-decreasing.";

        var parsed = await CompleteJsonAsync<ResegResponse>(system, user, 16384, ct);
        if (parsed?.Lines is not { Count: > 0 })
            return null;

        var lines = new List<(double, string)>();
        double lastTime = 0;
        foreach (var l in parsed.Lines)
        {
            var text = l.Text?.Trim();
            if (string.IsNullOrEmpty(text))
                continue;
            var sw = Math.Clamp(l.StartWord, 0, words.Count - 1);
            var t = Math.Max(words[sw].Start, lastTime);
            lines.Add((t, text));
            lastTime = t;
        }
        return lines.Count > 0 ? lines : null;
    }

    private async Task<T?> CompleteJsonAsync<T>(string system, string user, int maxTokens, CancellationToken ct)
        where T : class
    {
        string? content;
        try
        {
            content = await CompleteRawAsync(system, user, maxTokens, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "LLM lyric alignment call failed; falling back to the heuristic split.");
            return null;
        }

        var json = ExtractJson(content);
        if (json is null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            // The model can truncate a long array at its token limit; salvage the complete elements
            // by trimming to the last closed object and re-closing the array.
            var repaired = RepairTruncatedArrayJson(json);
            if (repaired is null)
                return null;
            logger.LogWarning("LLM lyric alignment JSON was truncated; recovered the complete lines.");
            try { return JsonSerializer.Deserialize<T>(repaired, Json); }
            catch (JsonException) { return null; }
        }
    }

    /// <summary>POSTs a JSON chat-completion to the OpenRouter endpoint with reasoning OFF (fast path).</summary>
    private async Task<string?> CompleteRawAsync(string system, string user, int maxTokens, CancellationToken ct)
    {
        var creds = openRouter.CurrentValue;
        var model = options.CurrentValue.LlmModel;
        var url = $"{creds.BaseUrl.TrimEnd('/')}/chat/completions";

        var body = new ChatRequest(
            model,
            new[] { new ChatReqMessage("system", system), new ChatReqMessage("user", user) },
            Temperature: 0,
            MaxTokens: maxTokens,
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
        cts.CancelAfter(TimeSpan.FromSeconds(90));

        using var resp = await httpClient.SendAsync(req, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            var errorBody = await resp.Content.ReadAsStringAsync(cts.Token);
            // Never log the Authorization header; the URL/body carry no secret.
            logger.LogWarning("Lyric alignment LLM ({Model}) failed: {Status} {Body}",
                model, (int)resp.StatusCode, Truncate(errorBody, 300));
            return null;
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

    private static string NumberLines(IReadOnlyList<string> lines)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
            sb.Append(i).Append('\t').Append(lines[i]).Append('\n');
        return sb.ToString();
    }

    private static string WordStream(IReadOnlyList<TimedWord> words)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < words.Count; i++)
            sb.Append(i).Append(':').Append(words[i].Word).Append(' ');
        return sb.ToString().TrimEnd();
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

    private sealed record AlignResponse(
        [property: JsonPropertyName("lines")] List<AlignLine>? Lines);

    private sealed record AlignLine(
        [property: JsonPropertyName("line")] int Line,
        [property: JsonPropertyName("startWord")] int StartWord);

    private sealed record ResegResponse(
        [property: JsonPropertyName("lines")] List<ResegLine>? Lines);

    private sealed record ResegLine(
        [property: JsonPropertyName("startWord")] int StartWord,
        [property: JsonPropertyName("text")] string? Text);
}
