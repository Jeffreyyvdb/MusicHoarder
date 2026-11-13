using Microsoft.AspNetCore.Http;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.Middleware;

namespace MusicHoarder.Api.Tests.Auth;

/// <summary>
/// The session cookie is the one transport: the browser and the app shell's web view both send
/// it. A bearer header (what the retired native Android client sent) authenticates nothing.
/// </summary>
public class AuthenticationMiddlewareTests
{
    private static readonly Guid ActiveSessionId = Guid.NewGuid();
    private static readonly Guid DeadSessionId = Guid.NewGuid();

    [Fact]
    public async Task Cookie_WithActiveSession_SetsCurrentUser()
    {
        var (ctx, cookies) = NewContext();
        ctx.Request.Headers.Cookie = $"{cookies.CookieName}={FakeCookieService.ProtectStatic(ActiveSessionId)}";

        await RunAsync(ctx, cookies);

        var user = Assert.IsType<CurrentUser>(ctx.Items[HttpContextCurrentUserAccessor.HttpContextItemKey]);
        Assert.Equal(WellKnownUsers.OwnerId, user.Id);
        // Recorded for a long-lived request (the playback stream) to re-check later.
        Assert.Equal(ActiveSessionId, ctx.Items[AuthenticationMiddleware.SessionIdItemKey]);
    }

    [Fact]
    public async Task Cookie_DeadSession_IsCleared_AndLeavesRequestAnonymous()
    {
        var (ctx, cookies) = NewContext();
        ctx.Request.Headers.Cookie = $"{cookies.CookieName}={FakeCookieService.ProtectStatic(DeadSessionId)}";

        var nextCalled = await RunAsync(ctx, cookies);

        Assert.True(nextCalled);
        Assert.False(ctx.Items.ContainsKey(HttpContextCurrentUserAccessor.HttpContextItemKey));
        Assert.Equal(1, cookies.ClearCount);
    }

    [Fact]
    public async Task TamperedCookie_IsCleared_AndLeavesRequestAnonymous()
    {
        var (ctx, cookies) = NewContext();
        ctx.Request.Headers.Cookie = $"{cookies.CookieName}=tampered-value";

        var nextCalled = await RunAsync(ctx, cookies);

        Assert.True(nextCalled);
        Assert.Equal(1, cookies.ClearCount);
        Assert.False(ctx.Items.ContainsKey(HttpContextCurrentUserAccessor.HttpContextItemKey));
        Assert.False(ctx.Items.ContainsKey(AuthenticationMiddleware.SessionIdItemKey));
    }

    [Fact]
    public async Task BearerHeader_WithALiveSessionId_AuthenticatesNothing()
    {
        var (ctx, cookies) = NewContext();
        ctx.Request.Headers.Authorization = $"Bearer {FakeCookieService.ProtectStatic(ActiveSessionId)}";

        var nextCalled = await RunAsync(ctx, cookies);

        Assert.True(nextCalled);
        Assert.False(ctx.Items.ContainsKey(HttpContextCurrentUserAccessor.HttpContextItemKey));
        Assert.Equal(0, cookies.ClearCount);
    }

    // ── harness ───────────────────────────────────────────────────────────────

    private static (DefaultHttpContext Context, FakeCookieService Cookies) NewContext() =>
        (new DefaultHttpContext(), new FakeCookieService());

    private static async Task<bool> RunAsync(HttpContext ctx, FakeCookieService cookies)
    {
        var nextCalled = false;
        var middleware = new AuthenticationMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        await middleware.InvokeAsync(ctx, new FakeAuthService(), cookies);
        return nextCalled;
    }

    /// <summary>Reversible stand-in for data protection: token is a prefixed session id.</summary>
    private sealed class FakeCookieService : ISessionCookieService
    {
        private const string Prefix = "tok.";

        public int ClearCount { get; private set; }
        public string CookieName => "mh_session";

        public static string ProtectStatic(Guid sessionId) => Prefix + sessionId.ToString("N");

        public string Protect(Guid sessionId) => ProtectStatic(sessionId);

        public Guid? Unprotect(string cookieValue) =>
            cookieValue.StartsWith(Prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(cookieValue[Prefix.Length..], "N", out var id)
                ? id
                : null;

        public void Write(HttpContext context, Guid sessionId) { }

        public void Clear(HttpContext context) => ClearCount++;

        public string AltsCookieName => CookieName + "_alts";

        public IReadOnlyList<Guid> ReadAlts(HttpContext context) => [];

        public void WriteAlts(HttpContext context, IReadOnlyList<Guid> sessionIds) { }

        public void ClearAlts(HttpContext context) { }
    }

    private sealed class FakeAuthService : IAuthService
    {
        public Task<(Session Session, User User)?> ResolveSessionAsync(Guid sessionId, CancellationToken ct)
        {
            if (sessionId != ActiveSessionId)
                return Task.FromResult<(Session, User)?>(null);

            var user = new User
            {
                Id = WellKnownUsers.OwnerId,
                Email = "owner@test.local",
                EmailNormalized = "owner@test.local",
                DisplayName = "Owner",
                Role = UserRole.Admin,
            };
            var session = new Session
            {
                Id = sessionId,
                UserId = user.Id,
                IssuedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddDays(30),
            };
            return Task.FromResult<(Session, User)?>((session, user));
        }

        public Task<IReadOnlyList<(Session Session, User User)>> ResolveSessionsAsync(IReadOnlyCollection<Guid> sessionIds, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<(Session, User)>>([]);

        public Task<RequestLinkResult?> RequestLinkAsync(string email, string frontendBaseUrl, string? client, string? ip, string? userAgent, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<Session?> ConsumeLinkAsync(string rawToken, string? ip, string? userAgent, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<Session?> StartDemoSessionAsync(string? ip, string? userAgent, CancellationToken ct)
            => throw new NotSupportedException();

        public Task RevokeAsync(Guid sessionId, bool allForUser, CancellationToken ct)
            => Task.CompletedTask;

        public Task<InviteMintResult?> CreateOrRotateInviteAsync(Guid ownerUserId, string email, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<InvitePeekResult?> PeekInviteAsync(string rawToken, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<Session?> AcceptInviteAsync(string rawToken, string? ip, string? userAgent, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
