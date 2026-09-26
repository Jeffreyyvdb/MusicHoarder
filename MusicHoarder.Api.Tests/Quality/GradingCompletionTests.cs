using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Quality;
using SongQualityVerdict = MusicHoarder.Api.Persistence.SongQualityVerdict;

namespace MusicHoarder.Api.Tests.Quality;

public class GradingCompletionTests
{
    private const string Grade = """{"score": 82, "verdict": "good", "summary": "Consistent."}""";

    // Replies gpt-oss gave in production (JSON mode, some OpenRouter providers): its reasoning cut
    // off at the first quote inside it and closed, and a leaked "final" channel padded with whitespace.
    private const string AnalysisCutAtQuote =
        "{\"analysis\":\"We need to grade the enrichment result. The file has embedded tags: title \"\n\n}";

    private static readonly string FinalPaddedWithWhitespace = "{\"final{\"" + new string(' ', 2000);

    // What the client hands back when a reasoning model ran out of tokens mid-thought: its
    // reasoning, whose quoted dossier fragments parse as objects without a grade.
    private const string ReasoningCutOff =
        """The change log has {"field": "album", "proposed": true, "applied": false} so the tag was kept. Next, the destination""";

    private static readonly IReadOnlyList<ChatMessage> Messages = [new("user", "grade")];

    [Fact]
    public async Task A_first_reply_with_a_grade_is_read_without_asking_again()
    {
        var client = new ScriptedChatClient(new ChatCompletionResult(Grade, 10, 10, "stop"));

        var (grade, raw) = await RequestAsync(client);

        Assert.Equal(82, grade.Score);
        Assert.Equal(Grade, raw);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task Replies_without_a_grade_are_asked_for_again_with_the_same_budget()
    {
        var client = new ScriptedChatClient(
            new ChatCompletionResult(AnalysisCutAtQuote, 10, 30, "stop"),
            new ChatCompletionResult(FinalPaddedWithWhitespace, 10, 4096, "length"),
            new ChatCompletionResult(Grade, 10, 10, "stop"));

        var (grade, raw) = await RequestAsync(client);

        Assert.Equal(SongQualityVerdict.Good, grade.Verdict);
        Assert.Equal(Grade, raw);
        // The whitespace reply was cut off in its answer: more budget would only buy more whitespace.
        Assert.Equal([4096, 4096, 4096], client.Requests.Select(r => r.MaxTokens));
    }

    [Fact]
    public async Task Reasoning_that_used_up_the_budget_is_asked_for_again_with_twice_the_budget()
    {
        var client = new ScriptedChatClient(
            new ChatCompletionResult(ReasoningCutOff, 10, 4096, "length", FromReasoning: true),
            new ChatCompletionResult(Grade, 10, 5000, "stop"));

        var (grade, _) = await RequestAsync(client);

        Assert.Equal(82, grade.Score);
        Assert.Equal([4096, 8192], client.Requests.Select(r => r.MaxTokens));
    }

    [Fact]
    public async Task A_doubled_budget_never_exceeds_what_the_configuration_accepts()
    {
        var client = new ScriptedChatClient(
            new ChatCompletionResult(ReasoningCutOff, 10, 12000, "length", FromReasoning: true),
            new ChatCompletionResult(ReasoningCutOff, 10, 16384, "length", FromReasoning: true),
            new ChatCompletionResult(Grade, 10, 10, "stop"));

        await RequestAsync(client, maxOutputTokens: 12000);

        Assert.Equal([12000, 16384, 16384], client.Requests.Select(r => r.MaxTokens));
    }

    [Fact]
    public async Task Replies_that_keep_running_out_while_reasoning_fail_naming_the_budget()
    {
        var client = new ScriptedChatClient(
            new ChatCompletionResult(ReasoningCutOff, 10, 4096, "length", FromReasoning: true),
            new ChatCompletionResult(ReasoningCutOff, 10, 8192, "length", FromReasoning: true),
            new ChatCompletionResult(ReasoningCutOff, 10, 16384, "length", FromReasoning: true));

        var ex = await Assert.ThrowsAsync<JsonException>(() => RequestAsync(client));

        Assert.Equal(
            "Model reply held no grade (no score or recognised verdict); "
            + "the model spent its whole 16384-token output budget reasoning and never wrote an answer",
            ex.Message);
        Assert.Equal(GradingCompletion.MaxAttempts, client.Requests.Count);
    }

    [Fact]
    public async Task Replies_that_never_hold_a_grade_fail_with_the_finish_reason()
    {
        var client = new ScriptedChatClient(
            new ChatCompletionResult(AnalysisCutAtQuote, 10, 30, "stop"),
            new ChatCompletionResult("{ }", 10, 10, "stop"),
            new ChatCompletionResult("{ }", 10, 10, "stop"));

        var ex = await Assert.ThrowsAsync<JsonException>(() => RequestAsync(client));

        Assert.Equal(
            "Model reply held no grade (no score or recognised verdict); asked 3 times (finish_reason=stop)",
            ex.Message);
        Assert.Equal(GradingCompletion.MaxAttempts, client.Requests.Count);
    }

    [Fact]
    public async Task A_transport_failure_is_not_retried_here()
    {
        // The client already retries 429/5xx; a failure that reaches here is the caller's to record.
        var client = new ScriptedChatClient { Throw = new HttpRequestException("Chat completion returned 401.") };

        await Assert.ThrowsAsync<HttpRequestException>(() => RequestAsync(client));
        Assert.Single(client.Requests);
    }

    private static Task<(GradingResult Grade, string RawContent)> RequestAsync(
        ScriptedChatClient client, int maxOutputTokens = 4096) =>
        GradingCompletion.RequestGradeAsync(
            client,
            Messages,
            new QualityGradingOptions { MaxOutputTokens = maxOutputTokens },
            NullLogger.Instance,
            CancellationToken.None);

    private sealed class ScriptedChatClient(params ChatCompletionResult[] replies) : IChatCompletionClient
    {
        public List<ChatCompletionRequest> Requests { get; } = [];
        public Exception? Throw { get; init; }
        public bool IsConfigured => true;

        public Task<ChatCompletionResult> CompleteAsync(ChatCompletionRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            if (Throw is not null)
                throw Throw;
            return Task.FromResult(replies[Requests.Count - 1]);
        }
    }
}
