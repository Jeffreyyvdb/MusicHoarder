using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Quality;

namespace MusicHoarder.Api.Tests.Quality;

public class OpenAiCompatibleChatClientTests
{
    private const string OkBody =
        """
        {
          "choices": [
            { "message": { "content": "{\"score\": 90, \"verdict\": \"excellent\"}" }, "finish_reason": "stop" }
          ],
          "usage": { "prompt_tokens": 50, "completion_tokens": 20 }
        }
        """;

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

    [Fact]
    public async Task Retries_after_429_with_retry_after_then_succeeds()
    {
        using var handler = new QueueHttpHandler(
            Response(HttpStatusCode.TooManyRequests, "{}", retryAfter: "0"),
            Response(HttpStatusCode.OK, OkBody));
        var client = CreateClient(new QualityGradingOptions
        {
            Enabled = true, ApiKey = "sk-test", BaseUrl = "https://openrouter.ai/api/v1",
            Model = "deepseek/deepseek-v4", MaxRetries = 3,
        }, handler);

        var result = await client.CompleteAsync(new ChatCompletionRequest([new ChatMessage("user", "grade")], 0.0, 700));

        Assert.Equal(2, handler.CallCount);
        Assert.Equal(90, QualityGradingPrompt.Parse(result.Content).Score);
    }

    [Fact]
    public async Task Retries_after_5xx_without_retry_after_then_succeeds()
    {
        using var handler = new QueueHttpHandler(
            Response(HttpStatusCode.ServiceUnavailable, "{}"),
            Response(HttpStatusCode.OK, OkBody));
        var client = CreateClient(new QualityGradingOptions
        {
            Enabled = true, ApiKey = "sk-test", BaseUrl = "https://openrouter.ai/api/v1",
            Model = "deepseek/deepseek-v4", MaxRetries = 3,
        }, handler);

        var result = await client.CompleteAsync(new ChatCompletionRequest([new ChatMessage("user", "grade")], 0.0, 700));

        Assert.Equal(2, handler.CallCount);
        Assert.Equal(90, QualityGradingPrompt.Parse(result.Content).Score);
    }

    [Fact]
    public async Task Throws_with_status_when_retries_exhausted()
    {
        using var handler = new QueueHttpHandler(
            Response(HttpStatusCode.TooManyRequests, "{}", retryAfter: "0"),
            Response(HttpStatusCode.TooManyRequests, "{}", retryAfter: "0"),
            Response(HttpStatusCode.TooManyRequests, "{}", retryAfter: "0"));
        var client = CreateClient(new QualityGradingOptions
        {
            Enabled = true, ApiKey = "sk-test", BaseUrl = "https://openrouter.ai/api/v1",
            Model = "deepseek/deepseek-v4", MaxRetries = 2, // 3 attempts total
        }, handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.CompleteAsync(new ChatCompletionRequest([new ChatMessage("user", "grade")], 0.0, 700)));

        Assert.Equal(3, handler.CallCount);
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
    }

    [Fact]
    public async Task Sends_reasoning_cap_when_configured()
    {
        using var handler = new QueueHttpHandler(Response(HttpStatusCode.OK, OkBody));
        var client = CreateClient(new QualityGradingOptions
        {
            Enabled = true, ApiKey = "sk-test", BaseUrl = "https://openrouter.ai/api/v1",
            Model = "deepseek/deepseek-v4", ReasoningMaxTokens = 1500,
        }, handler);

        await client.CompleteAsync(new ChatCompletionRequest([new ChatMessage("user", "grade")], 0.0, 700));

        Assert.Contains("\"reasoning\":{\"max_tokens\":1500}", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task Omits_reasoning_when_zero()
    {
        using var handler = new QueueHttpHandler(Response(HttpStatusCode.OK, OkBody));
        var client = CreateClient(new QualityGradingOptions
        {
            Enabled = true, ApiKey = "sk-test", BaseUrl = "https://openrouter.ai/api/v1",
            Model = "openai/gpt-4o-mini", ReasoningMaxTokens = 0,
        }, handler);

        await client.CompleteAsync(new ChatCompletionRequest([new ChatMessage("user", "grade")], 0.0, 700));

        Assert.DoesNotContain("reasoning", handler.RequestBodies[0]);
    }

    private static OpenAiCompatibleChatClient CreateClient(HttpStatusCode status, string responseBody)
    {
        var opts = new QualityGradingOptions
        {
            Enabled = true, ApiKey = "sk-test", BaseUrl = "https://openrouter.ai/api/v1",
            Model = "deepseek/deepseek-v4-flash",
        };
        return CreateClient(opts, new QueueHttpHandler(Response(status, responseBody)));
    }

    private static OpenAiCompatibleChatClient CreateClient(QualityGradingOptions opts, QueueHttpHandler handler)
    {
        var httpClient = new HttpClient(handler);
        return new OpenAiCompatibleChatClient(
            httpClient, new TestOptionsMonitor(opts), NullLogger<OpenAiCompatibleChatClient>.Instance);
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body, string? retryAfter = null)
    {
        var resp = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (retryAfter is not null)
            resp.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return resp;
    }

    private sealed class QueueHttpHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public int CallCount { get; private set; }
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            RequestBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            if (_responses.Count == 0)
                throw new InvalidOperationException("QueueHttpHandler ran out of queued responses.");
            return _responses.Dequeue();
        }
    }

    private sealed class TestOptionsMonitor(QualityGradingOptions value) : IOptionsMonitor<QualityGradingOptions>
    {
        public QualityGradingOptions CurrentValue { get; } = value;
        public QualityGradingOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<QualityGradingOptions, string?> listener) => null;
    }
}
