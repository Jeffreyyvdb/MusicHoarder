using Microsoft.AspNetCore.Http;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.Middleware;

namespace MusicHoarder.Api.Tests.Auth;

/// <summary>
/// Covers the two session transports: the browser cookie and the <c>Authorization: Bearer</c>
/// token native clients send (same protected session id — see <see cref="BearerToken"/>).
/// </summary>
public class AuthenticationMiddlewareTests
{
    private static readonly Guid ActiveSessionId = Guid.NewGuid();
    private static readonly Guid DeadSessionId = Guid.NewGuid();

    [Fact]
    public async Task BearerToken_WithActiveSession_SetsCurrentUser()
    {
        var (ctx, cookies) = NewContext();
        ctx.Request.Headers.Authorization = $"Bearer {FakeCookieService.ProtectStatic(ActiveSessionId)}";

        await RunAsync(ctx, cookies);

        var user = Assert.IsType<CurrentUser>(ctx.Items[HttpContextCurrentUserAccessor.HttpContextItemKey]);
        Assert.Equal(WellKnownUsers.OwnerId, user.Id);
    }

    [Fact]
    public async Task BearerToken_SchemeIsCaseInsensitive()
    {
        var (ctx, cookies) = NewContext();
        ctx.Request.Headers.Authorization = $"bearer {FakeCookieService.ProtectStatic(ActiveSessionId)}";

        await RunAsync(ctx, cookies);

        Assert.NotNull(ctx.Items[HttpContextCurrentUserAccessor.HttpContextItemKey]);
    }

    [Fact]
    public async Task BearerToken_Garbage_LeavesRequestAnonymous_AndCallsNext()
    {
        var (ctx, cookies) = NewContext();
        ctx.Request.Headers.Authorization = "Bearer not-a-real-token";

        var nextCalled = await RunAsync(ctx, cookies);

        Assert.True(nextCalled);
        Assert.False(ctx.Items.ContainsKey(HttpContextCurrentUserAccessor.HttpContextItemKey));
    }

    [Fact]
    public async Task BearerToken_DeadSession_DoesNotClearCookie()
    {
        // There is no cookie to clear on a bearer-only request; clearing would emit a pointless
        // Set-Cookie delete for native clients.
        var (ctx, cookies) = NewContext();
        ctx.Request.Headers.Authorization = $"Bearer {FakeCookieService.ProtectStatic(DeadSessionId)}";

        await RunAsync(ctx, cookies);

        Assert.False(ctx.Items.ContainsKey(HttpContextCurrentUserAccessor.HttpContextItemKey));
        Assert.Equal(0, cookies.ClearCount);
    }

    [Fact]
    public async Task Cookie_TakesPrecedence_OverBearerHeader()
    {
        var (ctx, cookies) = NewContext();
        ctx.Request.Headers.Cookie = $"{cookies.CookieName}={FakeCookieService.ProtectStatic(ActiveSessionId)}";
        ctx.Request.Headers.Authorization = $"Bearer {FakeCookieService.ProtectStatic(DeadSessionId)}";

        await RunAsync(ctx, cookies);

        Assert.NotNull(ctx.Items[HttpContextCurrentUserAccessor.HttpContextItemKey]);
    }

    [Fact]
    public async Task TamperedCookie_IsCleared_ThenBearerStillAuthenticates()
    {
        var (ctx, cookies) = NewContext();
        ctx.Request.Headers.Cookie = $"{cookies.CookieName}=tampered-value";
        ctx.Request.Headers.Authorization = $"Bearer {FakeCookieService.ProtectStatic(ActiveSessionId)}";

        await RunAsync(ctx, cookies);

        Assert.Equal(1, cookies.ClearCount);
        Assert.NotNull(ctx.Items[HttpContextCurrentUserAccessor.HttpContextItemKey]);
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
                Role = UserRole.Owner,
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

        public Task<Session?> CreateDeviceSessionAsync(Guid userId, string? ip, string? userAgent, CancellationToken ct)
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
