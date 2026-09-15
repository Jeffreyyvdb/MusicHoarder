using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace MusicHoarder.Api.Tests.Spotify;

internal sealed record SentRequest(
    HttpMethod Method,
    string Url,
    string? AuthScheme,
    string? AuthParameter,
    string? UserAgent,
    string? Body);

/// <summary>
/// Replays scripted responses in order and records every request. The clients under test dispose
/// their <see cref="HttpRequestMessage"/>s, so what a test needs is captured at send time.
/// </summary>
internal sealed class ScriptedHttpHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();

    public List<SentRequest> Requests { get; } = [];

    public int SendCount => Requests.Count;

    public void EnqueueToken(string token = "tok", int expiresIn = 3600) =>
        EnqueueJson($$"""{"access_token":"{{token}}","token_type":"Bearer","expires_in":{{expiresIn}}}""");

    public void EnqueueJson(string json) => Enqueue(HttpStatusCode.OK, json);

    public void Enqueue(HttpStatusCode status, string body = "", TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (retryAfter is { } delay)
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        _responses.Enqueue(response);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new SentRequest(
            request.Method,
            request.RequestUri?.ToString() ?? "",
            request.Headers.Authorization?.Scheme,
            request.Headers.Authorization?.Parameter,
            request.Headers.UserAgent.ToString(),
            body));

        if (_responses.Count == 0)
            throw new InvalidOperationException($"No scripted response for {request.Method} {request.RequestUri}");
        return _responses.Dequeue();
    }
}
