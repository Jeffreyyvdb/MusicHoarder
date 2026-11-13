using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.RateLimiting;

namespace MusicHoarder.Api.Quality.Jev;

/// <summary>
/// Talks to OpenRouter's Decisions API with the quality grader's OpenRouter key. Retries the same
/// failures as <see cref="OpenAiCompatibleChatClient"/> (HTTP 429/5xx, a per-attempt timeout, a
/// dropped connection), honours <c>Retry-After</c>, and never retries when the caller cancels.
/// </summary>
public sealed class OpenRouterDecisionsClient(
    HttpClient httpClient,
    IOptionsMonitor<JevGradingOptions> options,
    IOptionsMonitor<QualityGradingOptions> gradingOptions,
    ILogger<OpenRouterDecisionsClient> logger) : IDecisionClient
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(10);

    public bool IsConfigured => IsTrialConfigured(options.CurrentValue, gradingOptions.CurrentValue);

    /// <summary>The trial runs only while quality grading itself is configured: it shares its key.</summary>
    public static bool IsTrialConfigured(JevGradingOptions jev, QualityGradingOptions grading) =>
        jev.Enabled
        && grading.IsConfigured
        && !string.IsNullOrWhiteSpace(jev.BaseUrl)
        && jev.ModelList.Count > 0;

    public async Task<DecisionResult> DecideAsync(DecisionRequest request, CancellationToken ct = default)
    {
        var opts = options.CurrentValue;
        var grading = gradingOptions.CurrentValue;
        if (!IsTrialConfigured(opts, grading))
            throw new InvalidOperationException("The grader trial is not configured (JevGrading disabled, or no QualityGrading API key).");

        var url = $"{opts.BaseUrl.TrimEnd('/')}/decisions";
        var body = BuildBody(request).ToJsonString();

        var maxAttempts = Math.Max(0, opts.MaxRetries) + 1;
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            using var httpReq = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", grading.ApiKey);
            if (!string.IsNullOrWhiteSpace(grading.Referer))
                httpReq.Headers.TryAddWithoutValidation("HTTP-Referer", grading.Referer);
            if (!string.IsNullOrWhiteSpace(grading.AppTitle))
                httpReq.Headers.TryAddWithoutValidation("X-Title", grading.AppTitle);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(opts.TimeoutSeconds));

            HttpStatusCode failureStatus;
            string? failureDetail;
            try
            {
                using var resp = await httpClient.SendAsync(httpReq, cts.Token);
                var text = await resp.Content.ReadAsStringAsync(cts.Token);
                if (resp.IsSuccessStatusCode)
                    return ParseResponse(text) with { Attempts = attempt + 1 };

                // Never log the Authorization header; the body here carries no secret.
                logger.LogWarning("Decisions request failed: {Status} {Body}", (int)resp.StatusCode, Truncate(text, 512));
                failureDetail = ExtractErrorMessage(text);

                if (attempt < maxAttempts - 1 && IsRetryable(resp.StatusCode))
                {
                    await Task.Delay(Backoff(resp, attempt), ct);
                    continue;
                }

                failureStatus = resp.StatusCode;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
            {
                if (attempt >= maxAttempts - 1)
                {
                    if (ex is OperationCanceledException)
                        throw new TimeoutException(
                            $"Decisions request timed out after {opts.TimeoutSeconds}s ({maxAttempts} attempt(s)).", ex);
                    throw;
                }

                logger.LogWarning(ex, "Transient error calling the Decisions API (attempt {Attempt}/{Max}).", attempt + 1, maxAttempts);
                await Task.Delay(Backoff(null, attempt), ct);
                continue;
            }

            var detail = string.IsNullOrWhiteSpace(failureDetail) ? "" : $" {failureDetail}";
            throw new HttpRequestException($"Decisions request returned {(int)failureStatus}.{detail}", null, failureStatus);
        }
    }

    /// <summary>The request body, in the Decisions API's shape (public for the client tests).</summary>
    public static JsonObject BuildBody(DecisionRequest request)
    {
        var questions = new JsonObject();
        foreach (var (key, q) in request.Questions)
        {
            JsonNode criteria = q.Type == DecisionQuestionType.Score
                ? new JsonArray(q.Options.Select(o => (JsonNode)JsonValue.Create(o.Value)!).ToArray())
                : new JsonObject(q.Options.Select(o => KeyValuePair.Create(o.Key, (JsonNode?)JsonValue.Create(o.Value))));

            questions[key] = new JsonObject
            {
                ["type"] = q.Type switch
                {
                    DecisionQuestionType.Noul => "noul",
                    DecisionQuestionType.Choice => "choice",
                    _ => "score",
                },
                ["instructions"] = q.Instructions,
                ["criteria"] = criteria,
            };
        }

        return new JsonObject
        {
            ["model"] = request.Model,
            ["state"] = request.State.DeepClone(),
            ["questions"] = questions,
        };
    }

    /// <summary>Reads a Decisions response (public for the client tests). Throws <see cref="JsonException"/> when it holds no answers.</summary>
    public static DecisionResult ParseResponse(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("answers", out var answers)
            || answers.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Decisions response held no answers.");
        }

        var parsed = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
        foreach (var prop in answers.EnumerateObject())
        {
            var a = prop.Value;
            if (a.ValueKind != JsonValueKind.Object) continue;
            parsed[prop.Name] = new DecisionAnswer(
                Type: String(a, "type") ?? "",
                Noul: Number(a, "noul"),
                Choice: String(a, "choice"),
                Score: Number(a, "score"),
                Confidence: Number(a, "confidence"),
                Probabilities: Probabilities(a));
        }

        int? inputTokens = null, outputTokens = null;
        decimal? cost = null;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            inputTokens = Int(usage, "input_tokens");
            outputTokens = Int(usage, "output_tokens");
            if (usage.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetDecimal(out var d))
                cost = d;
        }

        return new DecisionResult(parsed, String(root, "model"), String(root, "provider"),
            inputTokens, outputTokens, cost, root.Clone());
    }

    private static IReadOnlyDictionary<string, double>? Probabilities(JsonElement answer)
    {
        if (!answer.TryGetProperty("probabilities", out var p) || p.ValueKind != JsonValueKind.Object)
            return null;
        var dict = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var prop in p.EnumerateObject())
            if (prop.Value.ValueKind == JsonValueKind.Number)
                dict[prop.Name] = prop.Value.GetDouble();
        return dict;
    }

    private static string? String(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Number(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static int? Int(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static bool IsRetryable(HttpStatusCode status) => (int)status is 429 or (>= 500 and <= 599);

    private static TimeSpan Backoff(HttpResponseMessage? resp, int attempt)
    {
        if (resp is not null && RetryAfterHeader.Read(resp, DateTimeOffset.UtcNow) is { } wait && wait >= TimeSpan.Zero)
            return wait > MaxBackoff ? MaxBackoff : wait;
        var ms = Random.Shared.NextDouble() * 500.0 * Math.Pow(2, attempt);
        var t = TimeSpan.FromMilliseconds(ms);
        return t > MaxBackoff ? MaxBackoff : t;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

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
                && msg.GetString() is { Length: > 0 } m)
            {
                return Truncate(m, 400);
            }
        }
        catch (JsonException)
        {
            // not JSON — fall through to the raw body
        }
        return Truncate(body.Trim(), 400);
    }
}
