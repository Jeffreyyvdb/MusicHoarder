using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Quality;

namespace MusicHoarder.Api.Tests.Quality;

public class OpenAiCompatibleChatClientTests
{
    [Fact]
    public async Task Falls_back_to_reasoning_when_content_is_empty()
    {
        // A reasoning model that exhausted its token budget on reasoning: empty `content`,
        // but the JSON object the grader asked for is present in `reasoning`.
        const string body =
            """
            {
              "choices": [
                {
                  "message": { "content": "", "reasoning": "{\"score\": 80, \"verdict\": \"good\"}" },
                  "finish_reason": "length"
                }
              ],
              "usage": { "prompt_tokens": 100, "completion_tokens": 700 }
            }
            """;

        var client = CreateClient(HttpStatusCode.OK, body);

        var result = await client.CompleteAsync(
            new ChatCompletionRequest([new ChatMessage("user", "grade")], 0.0, 700));

        Assert.Equal(80, QualityGradingPrompt.Parse(result.Content).Score);
    }

    [Fact]
    public async Task Throws_with_finish_reason_when_content_and_reasoning_are_empty()
    {
        const string body =
            """
            {
              "choices": [
                { "message": { "content": "" }, "finish_reason": "length" }
              ],
              "usage": { "prompt_tokens": 100, "completion_tokens": 700 }
            }
            """;

        var client = CreateClient(HttpStatusCode.OK, body);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CompleteAsync(new ChatCompletionRequest([new ChatMessage("user", "grade")], 0.0, 700)));

        Assert.Contains("finish_reason=length", ex.Message);
        Assert.Contains("completion_tokens=700", ex.Message);
    }

    private static OpenAiCompatibleChatClient CreateClient(HttpStatusCode status, string responseBody)
    {
        var httpClient = new HttpClient(new FakeHttpHandler(status, responseBody));
        var opts = new TestOptionsMonitor(new QualityGradingOptions
        {
            Enabled = true,
            ApiKey = "sk-test",
            BaseUrl = "https://openrouter.ai/api/v1",
            Model = "deepseek/deepseek-v4-flash",
        });
        return new OpenAiCompatibleChatClient(httpClient, opts, NullLogger<OpenAiCompatibleChatClient>.Instance);
    }

    private sealed class FakeHttpHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private sealed class TestOptionsMonitor(QualityGradingOptions value) : IOptionsMonitor<QualityGradingOptions>
    {
        public QualityGradingOptions CurrentValue { get; } = value;
        public QualityGradingOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<QualityGradingOptions, string?> listener) => null;
    }
}
