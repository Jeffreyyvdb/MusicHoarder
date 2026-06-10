using System.Text.Json;
using Microsoft.AspNetCore.Http;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.Middleware;

namespace MusicHoarder.Api.Tests.Auth;

/// <summary>
/// The demo account is strictly read-only: <see cref="DemoReadOnlyMiddleware"/> denies every unsafe
/// HTTP method for a demo session (deny-by-default), allowing only safe reads and the two auth POSTs
/// the demo needs. Owners and anonymous requests pass through untouched.
/// </summary>
public class DemoReadOnlyMiddlewareTests
{
    [Theory]
    [InlineData("POST", "/songs/1/reset-enrichment")]
    [InlineData("PATCH", "/songs/1/manual-review")]
    [InlineData("DELETE", "/songs/1")]
    [InlineData("POST", "/enrichment/scan")]
    [InlineData("POST", "/api/quality/grade-all")]
    [InlineData("PUT", "/api/settings")]
    public async Task demo_unsafe_request_is_rejected_with_403(string method, string path)
    {
        var (ctx, nextCalled) = await InvokeAsync(Demo(), method, path);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Equal("demo_read_only", await ReadErrorAsync(ctx));
    }

    [Theory]
    [InlineData("GET", "/songs")]
    [InlineData("GET", "/songs/1/stream")]
    [InlineData("GET", "/songs/1/cover")]
    [InlineData("HEAD", "/songs")]
    [InlineData("GET", "/api/enrichment/progress")]
    public async Task demo_safe_request_passes_through(string method, string path)
    {
        var (ctx, nextCalled) = await InvokeAsync(Demo(), method, path);

        Assert.True(nextCalled());
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }

    [Theory]
    [InlineData("/api/auth/logout")]
    [InlineData("/api/auth/demo-login")]
    public async Task demo_may_post_to_allowlisted_auth_endpoints(string path)
    {
        var (_, nextCalled) = await InvokeAsync(Demo(), "POST", path);

        Assert.True(nextCalled());
    }

    [Fact]
    public async Task owner_unsafe_request_passes_through()
    {
        var owner = new CurrentUser(WellKnownUsers.OwnerId, "owner@example.com", UserRole.Owner, "Owner");

        var (_, nextCalled) = await InvokeAsync(owner, "DELETE", "/songs/1");

        Assert.True(nextCalled());
    }

    [Fact]
    public async Task anonymous_unsafe_request_passes_through()
    {
        // RequireAuthMiddleware (upstream) already rejects anonymous traffic; this middleware leaves it alone.
        var (_, nextCalled) = await InvokeAsync(null, "POST", "/songs/1/reset-enrichment");

        Assert.True(nextCalled());
    }

    private static CurrentUser Demo() =>
        new(WellKnownUsers.DemoId, "demo@example.com", UserRole.Demo, "Demo");

    private static async Task<(HttpContext Context, Func<bool> NextCalled)> InvokeAsync(
        CurrentUser? user, string method, string path)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();

        var called = false;
        var middleware = new DemoReadOnlyMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(ctx, new StubCurrentUserAccessor(user));
        return (ctx, () => called);
    }

    private static async Task<string?> ReadErrorAsync(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(ctx.Response.Body);
        return doc.RootElement.GetProperty("error").GetString();
    }

    private sealed class StubCurrentUserAccessor(CurrentUser? user) : ICurrentUserAccessor
    {
        public CurrentUser? User { get; } = user;
        public Guid UserId => User?.Id ?? Guid.Empty;
    }
}
