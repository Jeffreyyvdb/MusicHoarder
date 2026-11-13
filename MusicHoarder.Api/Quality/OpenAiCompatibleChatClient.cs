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
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        // Omit null request fields (e.g. `reasoning` when unset) rather than sending `null`.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

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
            request.JsonResponse ? new ResponseFormat("json_object") : null,
            opts.ReasoningMaxTokens > 0 ? new ReasoningConfig(opts.ReasoningMaxTokens) : null,
            // OpenRouter's usage accounting: puts the request's USD cost in usage.cost. Other
            // OpenAI-compatible endpoints may reject unknown fields, so it is sent to OpenRouter only.
            IsOpenRouter(opts.BaseUrl) ? new UsageConfig(true) : null);
        // Serialize once; the body is identical across retries.
        var bodyJson = JsonSerializer.Serialize(payload, Json);

        var (body, httpAttempts) = await RetryingJsonPost.SendAsync(
            httpClient, url, bodyJson, opts, opts.MaxRetries, opts.TimeoutSeconds, "Chat completion", logger, ct);
        var parsed = JsonSerializer.Deserialize<ChatResponseBody>(body, Json);
        var choice = parsed?.Choices?.FirstOrDefault();
        var content = choice?.Message?.Content;

        // Reasoning models (e.g. DeepSeek V4) write their chain-of-thought to a separate field and
        // the answer to `content`. If the token budget is exhausted by reasoning, `content` is empty
        // but the reasoning channel may still carry the JSON object the grader asked for — the parser
        // tolerates prose/fences, so fall back to it before giving up.
        var fromReasoning = false;
        if (string.IsNullOrWhiteSpace(content))
        {
            content = choice?.Message?.Reasoning ?? choice?.Message?.ReasoningContent;
            fromReasoning = true;
        }

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

        return new ChatCompletionResult(
            content, parsed?.Usage?.PromptTokens, parsed?.Usage?.CompletionTokens, choice?.FinishReason, fromReasoning,
            parsed?.Provider, parsed?.Usage?.Cost, httpAttempts);
    }

    internal static bool IsOpenRouter(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
        && (uri.Host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".openrouter.ai", StringComparison.OrdinalIgnoreCase));

    private record ChatRequestBody(
        string Model,
        List<ChatRequestMessage> Messages,
        double Temperature,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("response_format")] ResponseFormat? ResponseFormat,
        [property: JsonPropertyName("reasoning")] ReasoningConfig? Reasoning,
        [property: JsonPropertyName("usage")] UsageConfig? Usage);

    private record UsageConfig([property: JsonPropertyName("include")] bool Include);

    private record ChatRequestMessage(string Role, string Content);

    private record ResponseFormat([property: JsonPropertyName("type")] string Type);

    private record ReasoningConfig([property: JsonPropertyName("max_tokens")] int MaxTokens);

    private record ChatResponseBody(
        [property: JsonPropertyName("choices")] List<ChatResponseChoice>? Choices,
        [property: JsonPropertyName("usage")] ChatUsage? Usage,
        // OpenRouter names the upstream that served the request; other endpoints omit it.
        [property: JsonPropertyName("provider")] string? Provider);

    private record ChatResponseChoice(
        [property: JsonPropertyName("message")] ChatResponseMessage? Message,
        [property: JsonPropertyName("finish_reason")] string? FinishReason);

    private record ChatResponseMessage(
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("reasoning")] string? Reasoning,
        [property: JsonPropertyName("reasoning_content")] string? ReasoningContent);

    private record ChatUsage(
        [property: JsonPropertyName("prompt_tokens")] int? PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int? CompletionTokens,
        [property: JsonPropertyName("cost")] decimal? Cost);
}
