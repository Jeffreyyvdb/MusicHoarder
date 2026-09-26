using System.Text.Json;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Quality;

/// <summary>
/// One grading call, shared by the song and album graders: ask the model, read the grade, and ask
/// once more when the reply holds none.
/// </summary>
/// <remarks>
/// A reply without a grade is usually not the subject's fault. A reasoning model can spend the whole
/// output budget thinking and never write its answer, leaving only its reasoning, whose quoted dossier
/// fragments parse as objects without a score; or a model can leak a channel object in place of the
/// answer. Recorded as a failure, such a subject was retried later with the same budget and failed the
/// same way on every sweep, keeping "grading is failing" up. So the second request doubles the budget
/// when the first was cut off by it, and a reply that still holds no grade is logged with an excerpt
/// before the failure is raised, since nothing else keeps it.
/// </remarks>
public static class GradingCompletion
{
    /// <summary>Requests per grade before the subject is recorded as failed.</summary>
    public const int MaxAttempts = 2;

    // The ceiling of QualityGradingOptions.MaxOutputTokens, so a doubled budget stays a value the
    // configuration itself would accept.
    private const int MaxOutputTokensCeiling = 16384;

    private const int ExcerptChars = 500;

    /// <summary>
    /// Returns the parsed grade and the reply it came from. Throws <see cref="JsonException"/> when
    /// <see cref="MaxAttempts"/> replies held no grade, saying why when the output limit cut them off.
    /// </summary>
    public static async Task<(GradingResult Grade, string RawContent)> RequestGradeAsync(
        IChatCompletionClient client,
        IReadOnlyList<ChatMessage> messages,
        QualityGradingOptions opts,
        ILogger logger,
        CancellationToken ct)
    {
        var maxTokens = opts.MaxOutputTokens;
        for (var attempt = 1; ; attempt++)
        {
            var reply = await client.CompleteAsync(
                new ChatCompletionRequest(messages, opts.Temperature, maxTokens), ct);
            try
            {
                return (QualityGradingPrompt.Parse(reply.Content), reply.Content);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(
                    "Grading reply held no grade (attempt {Attempt}/{MaxAttempts}, finish_reason={FinishReason}, "
                    + "from reasoning={FromReasoning}, max_tokens={MaxTokens}, completion_tokens={CompletionTokens}): {Excerpt}",
                    attempt, MaxAttempts, reply.FinishReason ?? "?", reply.FromReasoning, maxTokens,
                    reply.CompletionTokens, Excerpt(reply.Content));

                if (attempt >= MaxAttempts)
                    throw new JsonException(Describe(ex.Message, reply, maxTokens), ex);

                if (reply.FinishReason == "length")
                    maxTokens = Math.Max(maxTokens, Math.Min(maxTokens * 2, MaxOutputTokensCeiling));
            }
        }
    }

    /// <summary>The parse error, plus what the last reply says about why it held no grade.</summary>
    private static string Describe(string parseError, ChatCompletionResult reply, int maxTokens)
    {
        var why = (reply.FinishReason, reply.FromReasoning) switch
        {
            ("length", true) =>
                $"the model spent its whole {maxTokens}-token output budget reasoning and never wrote an answer",
            ("length", false) => $"the reply was cut off at the {maxTokens}-token output limit",
            _ => $"asked {MaxAttempts} times (finish_reason={reply.FinishReason ?? "unknown"})",
        };
        return $"{parseError.TrimEnd('.')}; {why}";
    }

    private static string Excerpt(string content)
    {
        var flat = content.ReplaceLineEndings(" ");
        return flat.Length <= ExcerptChars ? flat : flat[..ExcerptChars] + "…";
    }
}
