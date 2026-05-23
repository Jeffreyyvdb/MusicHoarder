using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Quality;

/// <summary>
/// Talks to any OpenAI-compatible chat-completions endpoint (OpenRouter, OpenAI, Groq, Ollama, …).
/// Options are read per-call via <see cref="IOptionsMonitor{T}"/> so the model/key can change at
/// runtime without a restart.
/// </summary>
public class OpenAiCompatibleChatClient(
    HttpClient httpClient,
    IOptionsMonitor<QualityGradingOptions> options,
    ILogger<OpenAiCompatibleChatClient> logger) : IChatCompletionClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool IsConfigured => options.CurrentValue.IsConfigured;

    public async Task<ChatCompletionResult> CompleteAsync(ChatCompletionRequest request, CancellationToken ct = default)
    {
        var opts = options.CurrentValue;
        if (!opts.IsConfigured)
            throw new InvalidOperationException("Quality grading is not configured (missing BaseUrl/ApiKey).");

        var url = $"{opts.BaseUrl.TrimEnd('/')}/chat/completions";
        var payload = new ChatRequestBody(
            opts.Model,
            request.Messages.Select(m => new ChatRequestMessage(m.Role, m.Content)).ToList(),
            request.Temperature,
            request.MaxTokens,
            request.JsonResponse ? new ResponseFormat("json_object") : null);

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload, options: Json),
        };
        httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opts.ApiKey);
        if (!string.IsNullOrWhiteSpace(opts.Referer))
            httpReq.Headers.TryAddWithoutValidation("HTTP-Referer", opts.Referer);
        if (!string.IsNullOrWhiteSpace(opts.AppTitle))
            httpReq.Headers.TryAddWithoutValidation("X-Title", opts.AppTitle);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(opts.TimeoutSeconds));

        using var resp = await httpClient.SendAsync(httpReq, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            // Never log the Authorization header; the URL/body here carry no secret.
            logger.LogWarning("Chat completion failed: {Status} {Body}", (int)resp.StatusCode, Truncate(body, 512));
            throw new HttpRequestException($"Chat completion returned {(int)resp.StatusCode}.", null, resp.StatusCode);
        }

        var parsed = await resp.Content.ReadFromJsonAsync<ChatResponseBody>(Json, cts.Token);
        var choice = parsed?.Choices?.FirstOrDefault();
        var content = choice?.Message?.Content;

        // Reasoning models (e.g. DeepSeek V4) write their chain-of-thought to a separate field and
        // the answer to `content`. If the token budget is exhausted by reasoning, `content` is empty
        // but the reasoning channel may still carry the JSON object the grader asked for — the parser
        // tolerates prose/fences, so fall back to it before giving up.
        if (string.IsNullOrWhiteSpace(content))
            content = choice?.Message?.Reasoning ?? choice?.Message?.ReasoningContent;

        if (string.IsNullOrWhiteSpace(content))
        {
            var finishReason = choice?.FinishReason ?? "unknown";
            var completionTokens = parsed?.Usage?.CompletionTokens;
            logger.LogWarning(
                "Chat completion returned an empty message (finish_reason={FinishReason}, completion_tokens={CompletionTokens}). "
                + "A reasoning model may have exhausted MaxOutputTokens before producing content.",
                finishReason, completionTokens);
            throw new InvalidOperationException(
                $"Chat completion returned an empty message (finish_reason={finishReason}, completion_tokens={completionTokens?.ToString() ?? "?"}).");
        }

        return new ChatCompletionResult(content, parsed?.Usage?.PromptTokens, parsed?.Usage?.CompletionTokens);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private record ChatRequestBody(
        string Model,
        List<ChatRequestMessage> Messages,
        double Temperature,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("response_format")] ResponseFormat? ResponseFormat);

    private record ChatRequestMessage(string Role, string Content);

    private record ResponseFormat([property: JsonPropertyName("type")] string Type);

    private record ChatResponseBody(
        [property: JsonPropertyName("choices")] List<ChatResponseChoice>? Choices,
        [property: JsonPropertyName("usage")] ChatUsage? Usage);

    private record ChatResponseChoice(
        [property: JsonPropertyName("message")] ChatResponseMessage? Message,
        [property: JsonPropertyName("finish_reason")] string? FinishReason);

    private record ChatResponseMessage(
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("reasoning")] string? Reasoning,
        [property: JsonPropertyName("reasoning_content")] string? ReasoningContent);

    private record ChatUsage(
        [property: JsonPropertyName("prompt_tokens")] int? PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int? CompletionTokens);
}
