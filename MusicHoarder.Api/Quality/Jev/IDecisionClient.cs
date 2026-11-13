using System.Text.Json;
using System.Text.Json.Nodes;

namespace MusicHoarder.Api.Quality.Jev;

/// <summary>The three question types the grader uses (OpenRouter's Decisions API also has <c>box</c>, for images).</summary>
public enum DecisionQuestionType
{
    /// <summary>Probability that a condition holds.</summary>
    Noul,
    /// <summary>One option from a named set, with a probability per option.</summary>
    Choice,
    /// <summary>A position on an ordered list of levels, with a probability per level.</summary>
    Score,
}

/// <summary>
/// One typed question. <see cref="Options"/> carries the criteria: for <see cref="DecisionQuestionType.Noul"/>
/// the keys <c>true</c> and <c>false</c>, for <see cref="DecisionQuestionType.Choice"/> one key per option,
/// for <see cref="DecisionQuestionType.Score"/> the levels in order, lowest first (their keys are ignored).
/// </summary>
public sealed record DecisionQuestion(
    DecisionQuestionType Type,
    string Instructions,
    IReadOnlyList<KeyValuePair<string, string>> Options);

public sealed record DecisionRequest(
    string Model,
    JsonNode State,
    IReadOnlyDictionary<string, DecisionQuestion> Questions);

/// <summary>
/// One answer. <see cref="Noul"/> is set for a noul question; <see cref="Choice"/> and
/// <see cref="Probabilities"/> (per option) for a choice; <see cref="Score"/> (the probability-weighted
/// level position, 0 = first level) and <see cref="Probabilities"/> (keyed by level index) for a score.
/// </summary>
public sealed record DecisionAnswer(
    string Type,
    double? Noul = null,
    string? Choice = null,
    double? Score = null,
    double? Confidence = null,
    IReadOnlyDictionary<string, double>? Probabilities = null);

public sealed record DecisionResult(
    IReadOnlyDictionary<string, DecisionAnswer> Answers,
    string? Model,
    string? Provider,
    int? InputTokens,
    int? OutputTokens,
    decimal? CostUsd,
    /// <summary>The response body, kept for debugging the mapping.</summary>
    JsonElement Raw)
{
    /// <summary>Requests the client made for this result, retries included.</summary>
    public int Attempts { get; init; } = 1;
}

/// <summary>
/// Client for OpenRouter's Decisions API (<c>POST /api/alpha/decisions</c>), which serves Jev and the
/// other decision models. Model-agnostic: the model id is part of each request, so another arm is
/// configuration only.
/// </summary>
public interface IDecisionClient
{
    /// <summary>True when the trial is enabled and an OpenRouter key is configured.</summary>
    bool IsConfigured { get; }

    Task<DecisionResult> DecideAsync(DecisionRequest request, CancellationToken ct = default);
}
