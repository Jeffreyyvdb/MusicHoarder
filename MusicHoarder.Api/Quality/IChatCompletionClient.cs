namespace MusicHoarder.Api.Quality;

public record ChatMessage(string Role, string Content);

public record ChatCompletionRequest(
    IReadOnlyList<ChatMessage> Messages,
    double Temperature,
    int MaxTokens,
    bool JsonResponse = true);

public record ChatCompletionResult(string Content, int? PromptTokens, int? CompletionTokens);

/// <summary>
/// Minimal client for an OpenAI-compatible <c>/chat/completions</c> endpoint. One method, no
/// streaming — the grader only needs a single JSON object back per call.
/// </summary>
public interface IChatCompletionClient
{
    /// <summary>True when a base URL + API key + model are configured. Callers no-op when false.</summary>
    bool IsConfigured { get; }

    Task<ChatCompletionResult> CompleteAsync(ChatCompletionRequest request, CancellationToken ct = default);
}
