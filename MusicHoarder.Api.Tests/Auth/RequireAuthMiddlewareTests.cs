using Microsoft.AspNetCore.Http;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.Middleware;

namespace MusicHoarder.Api.Tests.Auth;

public class RequireAuthMiddlewareTests
{
    // The machine-to-machine sync endpoints have no cookie session, so they must bypass this
    // middleware and let SyncApiKeyFilter be the gate. /like was missing from the allowlist once —
    // it 401'd before the sync-key filter ran, silently breaking cross-instance like propagation.
    [Theory]
    [InlineData("/api/sync/check")]
    [InlineData("/api/sync/upload")]
    [InlineData("/api/sync/like")]
    public async Task AllowlistedSyncPaths_PassThrough_WithoutASession(string path)
    {
        var (ctx, nextCalled) = await RunAsync(path, user: null);

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task SyncStatus_IsNotAllowlisted_Requires401_WhenAnonymous()
    {
        // /api/sync/status is a normal cookie-authed owner read — deliberately NOT in the allowlist.
        var (ctx, nextCalled) = await RunAsync("/api/sync/status", user: null);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task ProtectedPath_401_WhenAnonymous()
    {
        var (ctx, nextCalled) = await RunAsync("/api/songs", user: null);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    // The anonymous invite surface (peek + accept) must work with no session at all; the
    // owner-only management routes live under /api/friends and stay behind the cookie gate.
    [Theory]
    [InlineData("/api/invite/some-token")]
    [InlineData("/api/invite/accept")]
    public async Task InvitePaths_PassThrough_WithoutASession(string path)
    {
        var (ctx, nextCalled) = await RunAsync(path, user: null);

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    [Theory]
    [InlineData("/api/friends")]
    [InlineData("/api/friends/invites")]
    [InlineData("/api/shared/songs")]
    public async Task FriendManagementAndSharedReads_Require401_WhenAnonymous(string path)
    {
        var (ctx, nextCalled) = await RunAsync(path, user: null);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    private static async Task<(HttpContext Context, bool NextCalled)> RunAsync(string path, CurrentUser? user)
    {
        var nextCalled = false;
        var middleware = new RequireAuthMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        await middleware.InvokeAsync(ctx, new FakeCurrentUserAccessor(user));
        return (ctx, nextCalled);
    }

    private sealed class FakeCurrentUserAccessor(CurrentUser? user) : ICurrentUserAccessor
    {
        public CurrentUser? User => user;
        public Guid UserId => user?.Id ?? Guid.Empty;
    }
}
