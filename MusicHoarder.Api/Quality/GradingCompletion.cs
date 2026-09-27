using System.Text.Json;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Quality;

/// <summary>
/// One grading call, shared by the song and album graders: ask the model, read the grade, and ask
/// again when the reply holds none.
/// </summary>
/// <remarks>
/// A reply without a grade is rarely the subject's fault. gpt-oss, served by some OpenRouter providers
/// in JSON mode, writes its reasoning into the answer: <c>{"analysis":"We need to grade…</c>, cut off
/// at the first quote inside the reasoning and closed there, or <c>{"final{"</c> followed by
/// whitespace up to the output limit, or <c>{ }</c>. In production that was 3–15% of replies a week,
/// and independent per request: after such a reply, the next request for the same song held no grade
/// only 6% of the time. Recorded as a failure, the subject waited out the sweep's backoff and kept
/// "grading is failing" up meanwhile, so the request is repeated here instead: three attempts turn a
/// few percent of failed songs into a few in ten thousand.
/// <para>
/// A reasoning model can also spend the whole budget thinking and never answer, leaving only its
/// reasoning channel (<see cref="ChatCompletionResult.FromReasoning"/>); that attempt is repeated with
/// twice the budget. A reply cut off in its answer is not: more budget there buys more of whatever
/// filled it. Each reply without a grade is logged with an excerpt and its provider, since the failure
/// record keeps only the message.
/// </para>
/// </remarks>
public static class GradingCompletion
{
    /// <summary>Requests per grade before the subject is recorded as failed.</summary>
    public const int MaxAttempts = 3;

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
                    "Grading reply held no grade (attempt {Attempt}/{MaxAttempts}, provider={Provider}, "
                    + "finish_reason={FinishReason}, from reasoning={FromReasoning}, max_tokens={MaxTokens}, "
                    + "completion_tokens={CompletionTokens}): {Excerpt}",
                    attempt, MaxAttempts, reply.Provider ?? "?", reply.FinishReason ?? "?", reply.FromReasoning,
                    maxTokens, reply.CompletionTokens, Excerpt(reply.Content));

                if (attempt >= MaxAttempts)
                    throw new JsonException(Describe(ex.Message, reply, maxTokens), ex);

                if (RanOutWhileReasoning(reply))
                    maxTokens = Math.Max(maxTokens, Math.Min(maxTokens * 2, MaxOutputTokensCeiling));
            }
        }
    }

    private static bool RanOutWhileReasoning(ChatCompletionResult reply) =>
        reply.FromReasoning && reply.FinishReason == "length";

    /// <summary>The parse error, plus what the last reply says about why it held no grade.</summary>
    private static string Describe(string parseError, ChatCompletionResult reply, int maxTokens)
    {
        var why = RanOutWhileReasoning(reply)
            ? $"the model spent its whole {maxTokens}-token output budget reasoning and never wrote an answer"
            : $"asked {MaxAttempts} times (finish_reason={reply.FinishReason ?? "unknown"})";
        return $"{parseError.TrimEnd('.')}; {why}";
    }

    private static string Excerpt(string content)
    {
        var flat = content.ReplaceLineEndings(" ");
        return flat.Length <= ExcerptChars ? flat : flat[..ExcerptChars] + "…";
    }
}
