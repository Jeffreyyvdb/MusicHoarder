using System.Text.Json;
using Microsoft.AspNetCore.Http;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.Middleware;

namespace MusicHoarder.Api.Tests.Auth;

/// <summary>
/// Friends are strictly listen-only: <see cref="FriendReadOnlyMiddleware"/> denies every unsafe
/// HTTP method for a friend session (deny-by-default), allowing only safe reads and the auth
/// POSTs a friend legitimately needs. Owners, demo, and anonymous requests pass through untouched
/// — they have their own gates.
/// </summary>
public class FriendReadOnlyMiddlewareTests
{
    [Theory]
    [InlineData("POST", "/songs/1/like")]
    [InlineData("DELETE", "/songs/1/like")]
    [InlineData("POST", "/songs/1/played")]
    [InlineData("POST", "/api/enrichment/scan")]
    [InlineData("DELETE", "/songs/1")]
    [InlineData("PUT", "/api/settings")]
    [InlineData("POST", "/api/shares")]
    [InlineData("POST", "/api/friends/invites")]
    public async Task friend_unsafe_request_is_rejected_with_403(string method, string path)
    {
        var (ctx, nextCalled) = await InvokeAsync(TestCurrentUserAccessor.FriendUser, method, path);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Equal("friend_read_only", await ReadErrorAsync(ctx));
    }

    [Theory]
    [InlineData("GET", "/api/shared/songs")]
    [InlineData("GET", "/api/shared/songs/1/stream")]
    [InlineData("GET", "/api/shared/songs/1/cover")]
    [InlineData("GET", "/api/auth/me")]
    [InlineData("HEAD", "/api/shared/songs")]
    public async Task friend_safe_request_passes_through(string method, string path)
    {
        var (ctx, nextCalled) = await InvokeAsync(TestCurrentUserAccessor.FriendUser, method, path);

        Assert.True(nextCalled());
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }

    [Theory]
    [InlineData("/api/auth/logout")]
    [InlineData("/api/auth/device-token")]
    [InlineData("/api/auth/webauthn/authenticate/begin")]
    [InlineData("/api/auth/webauthn/authenticate/complete")]
    [InlineData("/api/invite/accept")]
    public async Task friend_may_post_to_allowlisted_auth_endpoints(string path)
    {
        var (_, nextCalled) = await InvokeAsync(TestCurrentUserAccessor.FriendUser, "POST", path);

        Assert.True(nextCalled());
    }

    [Fact]
    public async Task owner_unsafe_request_passes_through()
    {
        var (_, nextCalled) = await InvokeAsync(TestCurrentUserAccessor.OwnerUser, "DELETE", "/songs/1");

        Assert.True(nextCalled());
    }

    [Fact]
    public async Task demo_unsafe_request_passes_through_here()
    {
        // The demo has its own middleware; this one must not double-handle it.
        var (_, nextCalled) = await InvokeAsync(TestCurrentUserAccessor.DemoUser, "DELETE", "/songs/1");

        Assert.True(nextCalled());
    }

    [Fact]
    public async Task anonymous_unsafe_request_passes_through()
    {
        // RequireAuthMiddleware (upstream) already rejects anonymous traffic; this middleware leaves it alone.
        var (_, nextCalled) = await InvokeAsync(null, "POST", "/songs/1/reset-enrichment");

        Assert.True(nextCalled());
    }

    private static async Task<(HttpContext Context, Func<bool> NextCalled)> InvokeAsync(
        CurrentUser? user, string method, string path)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();

        var called = false;
        var middleware = new FriendReadOnlyMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(ctx, new TestCurrentUserAccessor(user));
        return (ctx, () => called);
    }

    private static async Task<string?> ReadErrorAsync(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(ctx.Response.Body);
        return doc.RootElement.GetProperty("error").GetString();
    }
}
