namespace MusicHoarder.Api.Quality;

public record ChatMessage(string Role, string Content);

public record ChatCompletionRequest(
    IReadOnlyList<ChatMessage> Messages,
    double Temperature,
    int MaxTokens,
    bool JsonResponse = true);

/// <param name="FinishReason">Why the model stopped (<c>stop</c>, <c>length</c>…), as the endpoint reported it.</param>
/// <param name="FromReasoning">
/// True when the message's <c>content</c> was empty and <paramref name="Content"/> is the model's
/// reasoning channel instead — what a reasoning model leaves when it runs out of tokens mid-thought.
/// </param>
public record ChatCompletionResult(
    string Content,
    int? PromptTokens,
    int? CompletionTokens,
    string? FinishReason = null,
    bool FromReasoning = false);

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
