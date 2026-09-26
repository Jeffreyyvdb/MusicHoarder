using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Options;
using SongQualityVerdict = MusicHoarder.Api.Persistence.SongQualityVerdict;
using MusicHoarder.Api.Quality;

namespace MusicHoarder.Api.Tests.Quality;

public class GradingCompletionTests
{
    private const string Grade = """{"score": 82, "verdict": "good", "summary": "Consistent."}""";

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
    public async Task A_reply_without_a_grade_is_asked_for_once_more_with_the_same_budget()
    {
        var client = new ScriptedChatClient(
            new ChatCompletionResult("""{"analysis": "Looks right. Score 95."}""", 10, 10, "stop"),
            new ChatCompletionResult(Grade, 10, 10, "stop"));

        var (grade, raw) = await RequestAsync(client);

        Assert.Equal(SongQualityVerdict.Good, grade.Verdict);
        Assert.Equal(Grade, raw);
        Assert.Equal([4096, 4096], client.Requests.Select(r => r.MaxTokens));
    }

    [Fact]
    public async Task A_reply_the_output_limit_cut_off_is_asked_for_again_with_twice_the_budget()
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
            new ChatCompletionResult(Grade, 10, 10, "stop"));

        await RequestAsync(client, maxOutputTokens: 12000);

        Assert.Equal([12000, 16384], client.Requests.Select(r => r.MaxTokens));
    }

    [Fact]
    public async Task Two_replies_cut_off_while_reasoning_fail_naming_the_budget()
    {
        var client = new ScriptedChatClient(
            new ChatCompletionResult(ReasoningCutOff, 10, 4096, "length", FromReasoning: true),
            new ChatCompletionResult(ReasoningCutOff, 10, 8192, "length", FromReasoning: true));

        var ex = await Assert.ThrowsAsync<JsonException>(() => RequestAsync(client));

        Assert.Equal(
            "Model reply held no grade (no score or recognised verdict); "
            + "the model spent its whole 8192-token output budget reasoning and never wrote an answer",
            ex.Message);
        Assert.Equal(GradingCompletion.MaxAttempts, client.Requests.Count);
    }

    [Fact]
    public async Task Two_complete_replies_without_a_grade_fail_with_the_finish_reason()
    {
        var client = new ScriptedChatClient(
            new ChatCompletionResult("{ }", 10, 10, "stop"),
            new ChatCompletionResult("{ }", 10, 10, "stop"));

        var ex = await Assert.ThrowsAsync<JsonException>(() => RequestAsync(client));

        Assert.Equal(
            "Model reply held no grade (no score or recognised verdict); asked 2 times (finish_reason=stop)",
            ex.Message);
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
