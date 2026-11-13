using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Quality.Jev;
using MusicHoarder.Api.Tests.Spotify;

namespace MusicHoarder.Api.Tests.Quality.Jev;

public class OpenRouterDecisionsClientTests
{
    // The response sample from OpenRouter's "Submit a Decisions request" reference, verbatim.
    private const string ReferenceResponse =
        """
        {
          "answers": {
            "is_bug": { "noul": 0.96, "type": "noul" },
            "team": {
              "choice": "payments", "confidence": 0.75,
              "probabilities": { "account": 0, "frontend": 0.16, "payments": 0.84 },
              "type": "choice"
            },
            "urgency": {
              "confidence": 0.99,
              "legend": { "0": "Can wait for the next release", "1": "Should be fixed this week", "2": "Blocking revenue right now" },
              "probabilities": { "0": 0, "1": 0.01, "2": 0.99 },
              "score": 1.99, "type": "score"
            }
          },
          "id": "gen-dec-1789738314-X5e5eKGQdvR9rblyX250",
          "model": "typesafe/jev-1.13-20260917",
          "provider": "TypeSafe",
          "usage": { "cost": 0.000019992, "input_tokens": 476, "output_tokens": 70 }
        }
        """;

    [Fact]
    public void Reads_the_api_reference_response()
    {
        var result = OpenRouterDecisionsClient.ParseResponse(ReferenceResponse);

        Assert.Equal(0.96, result.Answers["is_bug"].Noul);
        Assert.Equal("payments", result.Answers["team"].Choice);
        Assert.Equal(0.84, result.Answers["team"].Probabilities!["payments"]);
        Assert.Equal(1.99, result.Answers["urgency"].Score);
        Assert.Equal(0.99, result.Answers["urgency"].Probabilities!["2"]);
        Assert.Equal("typesafe/jev-1.13-20260917", result.Model);
        Assert.Equal("TypeSafe", result.Provider);
        Assert.Equal(476, result.InputTokens);
        Assert.Equal(70, result.OutputTokens);
        Assert.Equal(0.000019992m, result.CostUsd);
    }

    [Fact]
    public void A_response_without_answers_is_an_error()
    {
        Assert.Throws<JsonException>(() => OpenRouterDecisionsClient.ParseResponse("""{"error":{"message":"x"}}"""));
    }

    [Fact]
    public void Body_follows_the_api_reference_shape()
    {
        var body = OpenRouterDecisionsClient.BuildBody(new DecisionRequest(
            "typesafe/jev-1.14-20261014",
            new JsonObject { ["ticket"] = "blank screen" },
            new Dictionary<string, DecisionQuestion>
            {
                ["is_bug"] = new(DecisionQuestionType.Noul, "Is it a bug?", [new("true", "broken"), new("false", "a question")]),
                ["team"] = new(DecisionQuestionType.Choice, "Which team?", [new("payments", "billing"), new("frontend", "layout")]),
                ["urgency"] = new(DecisionQuestionType.Score, "How urgent?", [new("0", "Can wait"), new("1", "This week"), new("2", "Now")]),
            }));

        Assert.Equal("typesafe/jev-1.14-20261014", (string?)body["model"]);
        Assert.Equal("blank screen", (string?)body["state"]!["ticket"]);

        var noul = body["questions"]!["is_bug"]!;
        Assert.Equal("noul", (string?)noul["type"]);
        Assert.Equal("broken", (string?)noul["criteria"]!["true"]);
        Assert.Equal("a question", (string?)noul["criteria"]!["false"]);

        Assert.Equal("billing", (string?)body["questions"]!["team"]!["criteria"]!["payments"]);

        // A score's levels are an ordered array, lowest first.
        var levels = body["questions"]!["urgency"]!["criteria"]!.AsArray().Select(n => (string?)n).ToList();
        Assert.Equal(["Can wait", "This week", "Now"], levels);
    }

    [Fact]
    public async Task Retries_a_429_then_reports_two_attempts()
    {
        using var handler = new ScriptedHttpHandler();
        handler.Enqueue(HttpStatusCode.TooManyRequests, "{}", retryAfter: TimeSpan.Zero);
        handler.EnqueueJson(ReferenceResponse);
        var client = Create(handler);

        var result = await client.DecideAsync(Request());

        Assert.Equal(2, handler.SendCount);
        Assert.Equal(2, result.Attempts);
        Assert.Equal("https://openrouter.ai/api/alpha/decisions", handler.Requests[^1].Url);
        Assert.Equal("Bearer", handler.Requests[^1].AuthScheme);
        Assert.Equal("sk-test", handler.Requests[^1].AuthParameter);
    }

    [Fact]
    public async Task A_400_is_not_retried_and_carries_the_providers_reason()
    {
        using var handler = new ScriptedHttpHandler();
        handler.Enqueue(HttpStatusCode.BadRequest, """{"error":{"message":"box questions need jev-1.14"}}""");
        var client = Create(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.DecideAsync(Request()));

        Assert.Equal(1, handler.SendCount);
        Assert.Contains("box questions need jev-1.14", ex.Message);
    }

    [Fact]
    public void Is_off_without_the_quality_grading_key()
    {
        var client = new OpenRouterDecisionsClient(new HttpClient(),
            new TrialTestKit.Monitor<JevGradingOptions>(new JevGradingOptions()),
            new TrialTestKit.Monitor<QualityGradingOptions>(new QualityGradingOptions { ApiKey = "" }),
            NullLogger<OpenRouterDecisionsClient>.Instance);

        Assert.False(client.IsConfigured);
    }

    [Fact]
    public void Is_off_when_the_trial_is_disabled()
    {
        Assert.False(OpenRouterDecisionsClient.IsTrialConfigured(
            new JevGradingOptions { Enabled = false },
            new QualityGradingOptions { ApiKey = "sk-test" }));
        Assert.True(OpenRouterDecisionsClient.IsTrialConfigured(
            new JevGradingOptions(),
            new QualityGradingOptions { ApiKey = "sk-test" }));
    }

    private static DecisionRequest Request() => new(
        "typesafe/jev-1.14-20261014", new JsonObject { ["x"] = 1 },
        new Dictionary<string, DecisionQuestion>
        {
            ["is_bug"] = new(DecisionQuestionType.Noul, "?", [new("true", "a"), new("false", "b")]),
        });

    private static OpenRouterDecisionsClient Create(HttpMessageHandler handler) => new(
        new HttpClient(handler),
        new TrialTestKit.Monitor<JevGradingOptions>(new JevGradingOptions { MaxRetries = 2 }),
        new TrialTestKit.Monitor<QualityGradingOptions>(new QualityGradingOptions { ApiKey = "sk-test" }),
        NullLogger<OpenRouterDecisionsClient>.Instance);
}
