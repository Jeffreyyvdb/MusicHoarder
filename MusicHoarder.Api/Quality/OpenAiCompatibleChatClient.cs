using System.Net;
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
            opts.ReasoningMaxTokens > 0 ? new ReasoningConfig(opts.ReasoningMaxTokens) : null);
        // Serialize once; the body is identical across retries.
        var bodyJson = JsonSerializer.Serialize(payload, Json);

        var parsed = await SendWithRetryAsync(url, bodyJson, opts, ct);
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

    /// <summary>
    /// Posts the (already-serialized) request, retrying transient failures — HTTP 429/5xx, a per-call
    /// timeout, or a dropped connection — up to <see cref="QualityGradingOptions.MaxRetries"/> times.
    /// Honors <c>Retry-After</c> when present, otherwise backs off exponentially with jitter. Caller
    /// cancellation (host shutdown / request abort) is never retried.
    /// </summary>
    private async Task<ChatResponseBody?> SendWithRetryAsync(string url, string bodyJson, QualityGradingOptions opts, CancellationToken ct)
    {
        var maxAttempts = Math.Max(0, opts.MaxRetries) + 1;
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            using var httpReq = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(bodyJson, System.Text.Encoding.UTF8, "application/json"),
            };
            httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opts.ApiKey);
            if (!string.IsNullOrWhiteSpace(opts.Referer))
                httpReq.Headers.TryAddWithoutValidation("HTTP-Referer", opts.Referer);
            if (!string.IsNullOrWhiteSpace(opts.AppTitle))
                httpReq.Headers.TryAddWithoutValidation("X-Title", opts.AppTitle);

            // Each attempt gets its own timeout clock so a retry isn't starved by a prior attempt.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(opts.TimeoutSeconds));

            HttpStatusCode failureStatus;
            try
            {
                using var resp = await httpClient.SendAsync(httpReq, cts.Token);
                if (resp.IsSuccessStatusCode)
                    return await resp.Content.ReadFromJsonAsync<ChatResponseBody>(Json, cts.Token);

                var body = await resp.Content.ReadAsStringAsync(cts.Token);
                // Never log the Authorization header; the URL/body here carry no secret.
                logger.LogWarning("Chat completion failed: {Status} {Body}", (int)resp.StatusCode, Truncate(body, 512));

                if (attempt < maxAttempts - 1 && IsRetryableStatus(resp.StatusCode))
                {
                    var delay = ComputeBackoff(resp, attempt);
                    logger.LogWarning(
                        "Retrying chat completion after {Status} (attempt {Attempt}/{Max}) in {DelayMs}ms.",
                        (int)resp.StatusCode, attempt + 1, maxAttempts, (int)delay.TotalMilliseconds);
                    await Task.Delay(delay, ct);
                    continue;
                }

                // Non-retryable status, or retries exhausted. Captured here and thrown after the
                // try/catch so the transient-exception handler below doesn't re-catch it.
                failureStatus = resp.StatusCode;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Host shutdown / request abort — propagate, never retry.
                throw;
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
            {
                // Per-attempt timeout (our linked CTS fired, the caller's token did not) or a
                // dropped/refused connection — transient. Retry until the budget is exhausted.
                if (attempt >= maxAttempts - 1)
                {
                    if (ex is OperationCanceledException)
                        throw new TimeoutException(
                            $"Chat completion timed out after {opts.TimeoutSeconds}s ({maxAttempts} attempt(s)).", ex);
                    throw;
                }

                var delay = ComputeBackoff(null, attempt);
                logger.LogWarning(ex,
                    "Transient error calling chat completion (attempt {Attempt}/{Max}); retrying in {DelayMs}ms.",
                    attempt + 1, maxAttempts, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, ct);
                continue;
            }

            throw new HttpRequestException($"Chat completion returned {(int)failureStatus}.", null, failureStatus);
        }
    }

    private static bool IsRetryableStatus(HttpStatusCode status) => (int)status switch
    {
        429 => true,                 // Too Many Requests — back off and retry
        >= 500 and <= 599 => true,   // transient server-side (502/503/504, …)
        _ => false,
    };

    /// <summary>Honors <c>Retry-After</c> when the server sent one, else exponential backoff with full jitter, capped.</summary>
    private static TimeSpan ComputeBackoff(HttpResponseMessage? resp, int attempt)
    {
        var retryAfter = resp?.Headers.RetryAfter;
        if (retryAfter is not null)
        {
            if (retryAfter.Delta is { } delta && delta >= TimeSpan.Zero)
                return Cap(delta);
            if (retryAfter.Date is { } date)
            {
                var wait = date - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero)
                    return Cap(wait);
            }
        }

        var baseMs = 500.0 * Math.Pow(2, attempt);
        var jittered = Random.Shared.NextDouble() * baseMs;
        return Cap(TimeSpan.FromMilliseconds(jittered));
    }

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(10);
    private static TimeSpan Cap(TimeSpan t) => t > MaxBackoff ? MaxBackoff : t;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private record ChatRequestBody(
        string Model,
        List<ChatRequestMessage> Messages,
        double Temperature,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("response_format")] ResponseFormat? ResponseFormat,
        [property: JsonPropertyName("reasoning")] ReasoningConfig? Reasoning);

    private record ChatRequestMessage(string Role, string Content);

    private record ResponseFormat([property: JsonPropertyName("type")] string Type);

    private record ReasoningConfig([property: JsonPropertyName("max_tokens")] int MaxTokens);

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
