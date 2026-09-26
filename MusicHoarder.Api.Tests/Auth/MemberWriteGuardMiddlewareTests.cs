using System.Text.Json;
using Microsoft.AspNetCore.Http;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.Middleware;

namespace MusicHoarder.Api.Tests.Auth;

/// <summary>
/// Members are deny-by-default on every unsafe verb: <see cref="MemberWriteGuardMiddleware"/>
/// rejects anything that is not an explicit allowance, and an allowance may additionally require a
/// <see cref="Capability"/> the admin granted. Admins, demo, and anonymous requests pass through
/// untouched — they have their own gates.
/// </summary>
public class MemberWriteGuardMiddlewareTests
{
    private static CurrentUser MemberWith(Capability capabilities) =>
        new(TestUsers.FriendId, "member@test.local", UserRole.Member, "Member", capabilities);

    private static CurrentUser Listener => MemberWith(Capability.TrackListening);
    private static CurrentUser NoCapabilities => MemberWith(Capability.None);

    // --- Deny by default ---------------------------------------------------------------------

    [Theory]
    [InlineData("POST", "/api/enrichment/scan")]
    [InlineData("PUT", "/api/settings")]
    [InlineData("POST", "/api/shares")]
    [InlineData("POST", "/api/friends/invites")]
    [InlineData("PATCH", "/api/auth/me")] // rename-self is not a member allowance
    [InlineData("POST", "/api/wishlist")]
    [InlineData("DELETE", "/api/people/1")]
    public async Task Member_unsafe_request_is_rejected_with_403(string method, string path)
    {
        var (ctx, nextCalled) = await InvokeAsync(Listener, method, path);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Equal("member_write_denied", await ReadErrorAsync(ctx));
    }

    [Theory]
    [InlineData("DELETE", "/songs/1")]           // soft-delete the admin's row
    [InlineData("POST", "/songs/1/reset-enrichment")]
    [InlineData("POST", "/songs/1/lyrics/transcribe")]
    [InlineData("POST", "/songs/1/video/fetch")]
    [InlineData("DELETE", "/songs/1/video")]
    [InlineData("POST", "/songs/1/unlock")]
    [InlineData("DELETE", "/api/shared/songs")]
    public async Task Allowing_like_did_not_open_up_neighbouring_song_writes(string method, string path)
    {
        // THE test for this middleware. Allowing a member to like a track means allowing a write
        // under /songs/{id}/ — and a prefix rule would have allowed DELETE /songs/{id} with it,
        // letting a guest soft-delete the admin's library. Patterns are anchored per verb for
        // exactly this reason.
        var (ctx, nextCalled) = await InvokeAsync(Listener, method, path);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Member_still_cannot_reach_a_neighbouring_auth_write()
    {
        // Pins that widening the allowlist for `webauthn/register` did not open sibling auth writes.
        var (ctx, nextCalled) = await InvokeAsync(Listener, "POST", "/api/auth/demo-login");

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    // --- Reads ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "/songs")]
    [InlineData("GET", "/songs/1/stream")]
    [InlineData("GET", "/songs/1/cover")]
    [InlineData("GET", "/api/shared/songs")]
    [InlineData("GET", "/api/auth/me")]
    [InlineData("HEAD", "/songs")]
    public async Task Member_safe_request_passes_through(string method, string path)
    {
        var (ctx, nextCalled) = await InvokeAsync(NoCapabilities, method, path);

        Assert.True(nextCalled());
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }

    // --- Account self-management, inherent to holding an account ------------------------------

    [Theory]
    [InlineData("/api/auth/logout")]
    [InlineData("/api/auth/device-token")]
    [InlineData("/api/auth/switch")]
    [InlineData("/api/auth/webauthn/authenticate/begin")]
    [InlineData("/api/auth/webauthn/authenticate/complete")]
    [InlineData("/api/auth/webauthn/authenticate/native/begin")]
    [InlineData("/api/auth/webauthn/authenticate/native/complete")]
    [InlineData("/api/auth/webauthn/register/begin")]
    [InlineData("/api/auth/webauthn/register/complete")]
    [InlineData("/api/invite/accept")]
    public async Task Member_may_manage_their_own_session_without_any_capability(string path)
    {
        // These need no granted capability: they end the caller's own session, pair the caller's
        // own phone, or enrol a passkey on the caller's own account. Revoking every capability
        // must not lock someone out of their own login.
        var (_, nextCalled) = await InvokeAsync(NoCapabilities, "POST", path);

        Assert.True(nextCalled());
    }

    [Fact]
    public async Task Member_may_delete_their_own_passkey()
    {
        var (_, nextCalled) = await InvokeAsync(
            NoCapabilities, "DELETE", $"/api/auth/webauthn/credentials/{Guid.NewGuid()}");

        Assert.True(nextCalled());
    }

    // --- Chat and notifications ------------------------------------------------------------------

    [Theory]
    [InlineData("POST", "/api/chat/conversations")]
    [InlineData("POST", "/api/chat/send")]
    [InlineData("POST", "/api/chat/conversations/7c9e6679-7425-40de-944b-e07fc1f90ae7/messages")]
    [InlineData("POST", "/api/chat/conversations/7c9e6679-7425-40de-944b-e07fc1f90ae7/read")]
    [InlineData("POST", "/api/push/subscriptions")]
    [InlineData("POST", "/api/push/unsubscribe")]
    [InlineData("POST", "/api/push/test")]
    public async Task Member_may_chat_and_manage_their_notifications_without_any_capability(string method, string path)
    {
        // ChatService checks conversation membership on every route and a subscription is saved for
        // the caller only, so holding an account is the whole requirement.
        var (_, nextCalled) = await InvokeAsync(NoCapabilities, method, path);

        Assert.True(nextCalled());
    }

    [Theory]
    [InlineData("DELETE", "/api/chat/conversations/7c9e6679-7425-40de-944b-e07fc1f90ae7")]
    [InlineData("POST", "/api/chat/conversations/7c9e6679-7425-40de-944b-e07fc1f90ae7")]
    [InlineData("POST", "/api/chat/conversations/not-a-guid/messages")]
    [InlineData("POST", "/api/chat/conversations/7c9e6679-7425-40de-944b-e07fc1f90ae7/messages/1")]
    [InlineData("PUT", "/api/chat/send")]
    [InlineData("DELETE", "/api/push/subscriptions")]
    [InlineData("POST", "/api/chat/sendall")]
    public async Task Allowing_chat_did_not_open_up_neighbouring_writes(string method, string path)
    {
        var (ctx, nextCalled) = await InvokeAsync(NoCapabilities, method, path);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    // --- Playback sync between the member's own devices -----------------------------------------

    [Theory]
    [InlineData("/api/playback/state")]
    [InlineData("/api/playback/command")]
    public async Task Member_may_coordinate_their_own_devices_without_any_capability(string path)
    {
        // Keyed by the caller's own user id, like pairing a phone: nothing here reaches another
        // account, so holding an account is the whole requirement.
        var (_, nextCalled) = await InvokeAsync(NoCapabilities, "POST", path);

        Assert.True(nextCalled());
    }

    [Theory]
    [InlineData("POST", "/api/playback/state/x")]
    [InlineData("POST", "/api/playback/command/extra")]
    [InlineData("POST", "/api/playback/stateful")]
    [InlineData("POST", "/api/playback")]
    [InlineData("POST", "/api/playback/stream")]
    [InlineData("DELETE", "/api/playback/state")]
    [InlineData("PUT", "/api/playback/state")]
    [InlineData("PATCH", "/api/playback/command")]
    public async Task Playback_allowances_are_exact(string method, string path)
    {
        var (ctx, nextCalled) = await InvokeAsync(Listener, method, path);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Equal("member_write_denied", await ReadErrorAsync(ctx));
    }

    // --- Capability-gated allowances -----------------------------------------------------------

    [Theory]
    [InlineData("POST", "/songs/1/like")]
    [InlineData("DELETE", "/songs/1/like")]
    [InlineData("POST", "/songs/1/played")]
    [InlineData("POST", "/api/shared/songs/1/like")]     // deprecated alias
    [InlineData("DELETE", "/api/shared/songs/1/like")]
    [InlineData("POST", "/api/shared/songs/1/played")]
    public async Task Listening_writes_are_allowed_with_the_capability(string method, string path)
    {
        var (_, nextCalled) = await InvokeAsync(Listener, method, path);

        Assert.True(nextCalled());
    }

    [Theory]
    [InlineData("POST", "/songs/1/like")]
    [InlineData("POST", "/songs/1/played")]
    [InlineData("POST", "/api/shared/songs/1/like")]
    public async Task Listening_writes_are_denied_without_the_capability(string method, string path)
    {
        // The admin can switch a member's like and play tracking off; the guard is where that
        // takes effect, before any handler runs.
        var (ctx, nextCalled) = await InvokeAsync(NoCapabilities, method, path);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Equal("capability_required", await ReadErrorAsync(ctx));
    }

    [Fact]
    public async Task Multi_digit_song_ids_match_and_non_numeric_ones_do_not()
    {
        var (_, allowed) = await InvokeAsync(Listener, "POST", "/songs/123456/like");
        Assert.True(allowed());

        var (_, rejected) = await InvokeAsync(Listener, "POST", "/songs/abc/like");
        Assert.False(rejected());
    }

    // --- Public share-link beacons --------------------------------------------------------------

    [Theory]
    [InlineData("/api/share/AbC-123_x/visit")]
    [InlineData("/api/share/AbC-123_x/songs/42/play")]
    public async Task Member_may_send_a_share_links_open_and_play_beacons(string path)
    {
        // Anyone holding the link may send these; a member who is signed in when they open one
        // must not be the one visitor who goes uncounted.
        var (_, nextCalled) = await InvokeAsync(NoCapabilities, "POST", path);

        Assert.True(nextCalled());
    }

    [Theory]
    [InlineData("POST", "/api/shares")]                        // minting a link stays admin work
    [InlineData("DELETE", "/api/shares/1")]
    [InlineData("POST", "/api/share/tok/songs/abc/play")]
    [InlineData("POST", "/api/share/tok/extra/visit")]
    [InlineData("DELETE", "/api/share/tok/visit")]
    public async Task Allowing_the_beacons_did_not_open_up_share_management(string method, string path)
    {
        var (ctx, nextCalled) = await InvokeAsync(Listener, method, path);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    // --- Other account kinds are none of this middleware's business ---------------------------

    [Fact]
    public async Task Admin_unsafe_request_passes_through()
    {
        var (_, nextCalled) = await InvokeAsync(TestCurrentUserAccessor.OwnerUser, "DELETE", "/songs/1");

        Assert.True(nextCalled());
    }

    [Fact]
    public async Task Demo_unsafe_request_passes_through_here()
    {
        // The demo has its own middleware; this one must not double-handle it.
        var (_, nextCalled) = await InvokeAsync(TestCurrentUserAccessor.DemoUser, "DELETE", "/songs/1");

        Assert.True(nextCalled());
    }

    [Fact]
    public async Task Anonymous_unsafe_request_passes_through()
    {
        // RequireAuthMiddleware (upstream) already rejects anonymous traffic.
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
        var middleware = new MemberWriteGuardMiddleware(_ =>
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
