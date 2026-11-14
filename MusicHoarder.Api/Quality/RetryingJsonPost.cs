using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.RateLimiting;

namespace MusicHoarder.Api.Quality;

/// <summary>
/// The one retry policy for the OpenRouter calls the graders make (chat completions and decisions):
/// posts an already-serialized JSON body and retries transient failures — HTTP 429/5xx, a per-attempt
/// timeout, or a dropped connection. Honours <c>Retry-After</c> when present, otherwise backs off
/// exponentially with jitter, capped at 10 s. Caller cancellation (host shutdown, request abort) is
/// never retried.
/// </summary>
internal static class RetryingJsonPost
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(10);

    /// <param name="what">Names the call in log lines and exception messages ("Chat completion").</param>
    /// <returns>The successful response body and the number of requests it took.</returns>
    public static async Task<(string Body, int Attempts)> SendAsync(
        HttpClient httpClient,
        string url,
        string bodyJson,
        QualityGradingOptions auth,
        int maxRetries,
        int timeoutSeconds,
        string what,
        ILogger logger,
        CancellationToken ct)
    {
        var maxAttempts = Math.Max(0, maxRetries) + 1;
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            using var httpReq = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(bodyJson, Encoding.UTF8, "application/json"),
            };
            httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.ApiKey);
            if (!string.IsNullOrWhiteSpace(auth.Referer))
                httpReq.Headers.TryAddWithoutValidation("HTTP-Referer", auth.Referer);
            if (!string.IsNullOrWhiteSpace(auth.AppTitle))
                httpReq.Headers.TryAddWithoutValidation("X-Title", auth.AppTitle);

            // Each attempt gets its own timeout clock so a retry isn't starved by a prior attempt.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            HttpStatusCode failureStatus;
            string? failureDetail;
            try
            {
                using var resp = await httpClient.SendAsync(httpReq, cts.Token);
                var body = await resp.Content.ReadAsStringAsync(cts.Token);
                if (resp.IsSuccessStatusCode)
                    return (body, attempt + 1);

                // Never log the Authorization header; the URL/body here carry no secret.
                logger.LogWarning("{What} failed: {Status} {Body}", what, (int)resp.StatusCode, Truncate(body, 512));
                failureDetail = ExtractErrorMessage(body);

                if (attempt < maxAttempts - 1 && IsRetryableStatus(resp.StatusCode))
                {
                    var delay = ComputeBackoff(resp, attempt);
                    logger.LogWarning(
                        "Retrying {What} after {Status} (attempt {Attempt}/{Max}) in {DelayMs}ms.",
                        what, (int)resp.StatusCode, attempt + 1, maxAttempts, (int)delay.TotalMilliseconds);
                    await Task.Delay(delay, ct);
                    continue;
                }

                // Non-retryable status, or retries exhausted. Thrown after the try/catch so the
                // transient-exception handler below doesn't re-catch it.
                failureStatus = resp.StatusCode;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
            {
                // Per-attempt timeout (our linked CTS fired, the caller's token did not) or a
                // dropped/refused connection — transient. Retry until the budget is exhausted.
                if (attempt >= maxAttempts - 1)
                {
                    if (ex is OperationCanceledException)
                        throw new TimeoutException($"{what} timed out after {timeoutSeconds}s ({maxAttempts} attempt(s)).", ex);
                    throw;
                }

                var delay = ComputeBackoff(null, attempt);
                logger.LogWarning(ex,
                    "Transient error calling {What} (attempt {Attempt}/{Max}); retrying in {DelayMs}ms.",
                    what, attempt + 1, maxAttempts, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, ct);
                continue;
            }

            // Carry the provider's own explanation (e.g. "maximum context length is 131072 tokens")
            // into the message: it is stored on the grading run and shown in the UI's error banner,
            // where a bare status code says nothing about what to fix.
            var detail = string.IsNullOrWhiteSpace(failureDetail) ? "" : $" {failureDetail}";
            throw new HttpRequestException($"{what} returned {(int)failureStatus}.{detail}", null, failureStatus);
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
        if (resp is not null && RetryAfterHeader.Read(resp, DateTimeOffset.UtcNow) is { } wait && wait >= TimeSpan.Zero)
            return Cap(wait);

        var baseMs = 500.0 * Math.Pow(2, attempt);
        return Cap(TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * baseMs));
    }

    private static TimeSpan Cap(TimeSpan t) => t > MaxBackoff ? MaxBackoff : t;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>
    /// Pulls the human-readable reason out of an OpenAI-style error body (<c>{"error":{"message":"…"}}</c>),
    /// falling back to the raw body. Trimmed short — it ends up in an exception message that is
    /// persisted and rendered in the UI.
    /// </summary>
    private static string? ExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var err)
                && err.ValueKind == JsonValueKind.Object
                && err.TryGetProperty("message", out var msg)
                && msg.GetString() is { Length: > 0 } text)
            {
                return Truncate(text, 400);
            }
        }
        catch (JsonException)
        {
            // Not JSON (an HTML error page from a proxy, say) — fall through to the raw body.
        }

        return Truncate(body.Trim(), 400);
    }
}
