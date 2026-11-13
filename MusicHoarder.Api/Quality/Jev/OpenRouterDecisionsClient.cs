using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Quality.Jev;

/// <summary>
/// Talks to OpenRouter's Decisions API with the quality grader's OpenRouter key, under the same
/// retry policy as the chat client (<see cref="RetryingJsonPost"/>).
/// </summary>
public sealed class OpenRouterDecisionsClient(
    HttpClient httpClient,
    IOptionsMonitor<JevGradingOptions> options,
    IOptionsMonitor<QualityGradingOptions> gradingOptions,
    ILogger<OpenRouterDecisionsClient> logger) : IDecisionClient
{
    public bool IsConfigured => IsTrialConfigured(options.CurrentValue, gradingOptions.CurrentValue);

    /// <summary>
    /// The trial runs only while quality grading itself is configured, because it shares its key —
    /// and only when both point at OpenRouter: a key for another provider (OpenAI, Groq…) must never
    /// be sent to the Decisions API.
    /// </summary>
    public static bool IsTrialConfigured(JevGradingOptions jev, QualityGradingOptions grading) =>
        jev.Enabled
        && grading.IsConfigured
        && OpenAiCompatibleChatClient.IsOpenRouter(grading.BaseUrl)
        && OpenAiCompatibleChatClient.IsOpenRouter(jev.BaseUrl)
        && jev.ModelList.Count > 0;

    public async Task<DecisionResult> DecideAsync(DecisionRequest request, CancellationToken ct = default)
    {
        var opts = options.CurrentValue;
        var grading = gradingOptions.CurrentValue;
        if (!IsTrialConfigured(opts, grading))
            throw new InvalidOperationException("The grader trial is not configured (JevGrading disabled, or no QualityGrading API key).");

        var url = $"{opts.BaseUrl.TrimEnd('/')}/decisions";
        var (body, attempts) = await RetryingJsonPost.SendAsync(
            httpClient, url, BuildBody(request).ToJsonString(), grading, opts.MaxRetries, opts.TimeoutSeconds,
            "Decisions request", logger, ct);
        return ParseResponse(body) with { Attempts = attempts };
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
            inputTokens, outputTokens, cost);
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
}
