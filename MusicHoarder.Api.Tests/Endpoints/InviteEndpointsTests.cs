using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Endpoints;

/// <summary>
/// The native-client invite redemption: POST /api/invite/accept-token consumes the single-use
/// token exactly like the cookie-writing accept, but answers with a bearer the Android app can
/// pair with. The invite lifecycle itself (expiry, revocation, friend creation) is covered by
/// <see cref="Auth.InviteLifecycleTests"/> — this pins the endpoint's response shape and reuse.
/// </summary>
public class InviteEndpointsTests
{
    [Fact]
    public async Task AcceptToken_returns_bearer_and_consumes_the_invite()
    {
        var (svc, ctx) = MakeService();
        var cookies = new FakeCookieService();
        var minted = await svc.CreateOrRotateInviteAsync(TestUsers.OwnerId, "pal@example.com", default);

        var result = await InviteEndpoints.AcceptInviteToken(
            new InviteEndpoints.AcceptInviteBody(minted!.RawToken),
            new DefaultHttpContext(), svc, cookies, default);

        var value = Value(result);
        var token = GetProperty<string>(value, "AccessToken");
        Assert.StartsWith("protected:", token);
        Assert.Equal("Bearer", GetProperty<string>(value, "TokenType"));

        await using var db = ctx();
        var friend = await db.Users.SingleAsync(u => u.EmailNormalized == User.Normalize("pal@example.com"));
        Assert.Equal(UserRole.Member, friend.Role);
        var session = await db.Sessions.SingleAsync();
        Assert.Equal(friend.Id, session.UserId);
        // The bearer wraps the same server-side session row the cookie flow would have written.
        Assert.Equal($"protected:{session.Id:N}", token);
        Assert.NotNull((await db.Invites.SingleAsync()).ConsumedAtUtc);
    }

    [Fact]
    public async Task AcceptToken_second_call_is_400()
    {
        var (svc, _) = MakeService();
        var cookies = new FakeCookieService();
        var minted = await svc.CreateOrRotateInviteAsync(TestUsers.OwnerId, "pal@example.com", default);
        var body = new InviteEndpoints.AcceptInviteBody(minted!.RawToken);

        var first = await InviteEndpoints.AcceptInviteToken(body, new DefaultHttpContext(), svc, cookies, default);
        var second = await InviteEndpoints.AcceptInviteToken(body, new DefaultHttpContext(), svc, cookies, default);

        Assert.Equal(StatusCodes.Status200OK, ((IStatusCodeHttpResult)first).StatusCode);
        Assert.Equal(StatusCodes.Status400BadRequest, ((IStatusCodeHttpResult)second).StatusCode);
    }

    [Fact]
    public async Task AcceptToken_blank_token_is_400()
    {
        var (svc, _) = MakeService();

        var result = await InviteEndpoints.AcceptInviteToken(
            new InviteEndpoints.AcceptInviteBody("  "),
            new DefaultHttpContext(), svc, new FakeCookieService(), default);

        Assert.Equal(StatusCodes.Status400BadRequest, ((IStatusCodeHttpResult)result).StatusCode);
    }

    // -- helpers --

    private sealed class FakeCookieService : ISessionCookieService
    {
        public string CookieName => "mh_session";
        public string AltsCookieName => "mh_session_alts";
        public string Protect(Guid sessionId) => $"protected:{sessionId:N}";
        public Guid? Unprotect(string cookieValue) => null;
        public void Write(HttpContext context, Guid sessionId) { }
        public void Clear(HttpContext context) { }
        public IReadOnlyList<Guid> ReadAlts(HttpContext context) => [];
        public void WriteAlts(HttpContext context, IReadOnlyList<Guid> sessionIds) { }
        public void ClearAlts(HttpContext context) { }
    }

    private static (IAuthService Service, Func<MusicHoarderDbContext> CreateCtx) MakeService()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        using (var seed = new MusicHoarderDbContext(options))
        {
            seed.Users.Add(new User
            {
                Id = WellKnownUsers.OwnerId,
                Email = "owner@example.com",
                EmailNormalized = User.Normalize("owner@example.com"),
                DisplayName = "Owner",
                Role = UserRole.Admin,
                CreatedAtUtc = DateTime.UtcNow,
            });
            seed.SaveChanges();
        }

        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddScoped(sp => new MusicHoarderDbContext(sp.GetRequiredService<DbContextOptions<MusicHoarderDbContext>>()));
        var sp = services.BuildServiceProvider();

        var auth = new AuthService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new ConsoleMagicLinkSender(NullLogger<ConsoleMagicLinkSender>.Instance),
            new TestHostEnvironment("Development"),
            new TestOptionsMonitor<AuthOptions>(new AuthOptions
            {
                OwnerEmail = "owner@example.com",
                DemoUserEmail = "demo@example.com",
                MagicLinkTtlMinutes = 15,
                SessionLifetimeDays = 30,
                InviteTtlHours = 168,
            }),
            NullLogger<AuthService>.Instance);

        MusicHoarderDbContext CreateCtx() => new(options);
        return (auth, CreateCtx);
    }

    private sealed class TestHostEnvironment(string env) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = env;
        public string ApplicationName { get; set; } = "MusicHoarder.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T> where T : class
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private static object Value(IResult result)
        => result.GetType().GetProperty("Value")!.GetValue(result)!;

    private static T GetProperty<T>(object obj, string name)
    {
        var prop = obj.GetType().GetProperty(name);
        Assert.NotNull(prop);
        return (T)prop!.GetValue(obj)!;
    }
}
