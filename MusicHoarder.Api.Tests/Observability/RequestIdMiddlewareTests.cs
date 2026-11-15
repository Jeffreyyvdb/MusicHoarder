using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MusicHoarder.Api.Observability;

namespace MusicHoarder.Api.Tests.Observability;

/// <summary>
/// <see cref="RequestIdMiddleware"/> keeps the id the frontend proxy forwards (so a problem report's
/// <c>x-request-id</c> finds the API's log lines too), echoes it, and opens it as a log scope.
/// </summary>
public class RequestIdMiddlewareTests
{
    [Fact]
    public async Task keeps_the_forwarded_id_echoes_it_and_scopes_the_logs_with_it()
    {
        var logger = new ScopeCapturingLogger();
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers[RequestIdMiddleware.HeaderName] = "abc123XYZ_-";
        object? scopeSeenByEndpoint = null;

        var middleware = new RequestIdMiddleware(_ =>
        {
            scopeSeenByEndpoint = logger.CurrentScope;
            return Task.CompletedTask;
        }, logger);
        await middleware.InvokeAsync(ctx);

        Assert.Equal("abc123XYZ_-", ctx.Response.Headers[RequestIdMiddleware.HeaderName].ToString());
        // The simple console formatter prints a scope with ToString(); a JSON one reads its pairs.
        Assert.Equal("rid:abc123XYZ_-", scopeSeenByEndpoint?.ToString());
        var pairs = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(scopeSeenByEndpoint);
        Assert.Contains(pairs, p => p.Key == RequestIdMiddleware.ScopeKey && Equals(p.Value, "abc123XYZ_-"));
        Assert.Null(logger.CurrentScope);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("has space in it")]
    [InlineData("forged-line\nERROR: something")]
    [InlineData("trailing-newline\n")]
    public void replaces_a_missing_or_unsafe_id(string? incoming)
    {
        var id = RequestIdMiddleware.Resolve(incoming);

        Assert.NotEqual(incoming, id);
        Assert.Matches("^[A-Za-z0-9_-]{12}$", id);
    }

    private sealed class ScopeCapturingLogger : ILogger<RequestIdMiddleware>
    {
        public object? CurrentScope { get; private set; }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            CurrentScope = state;
            return new Pop(this);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }

        private sealed class Pop(ScopeCapturingLogger owner) : IDisposable
        {
            public void Dispose() => owner.CurrentScope = null;
        }
    }
}
