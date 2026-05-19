using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Auth;

public class AuthServiceTests
{
    [Fact]
    public async Task RequestLink_unknown_email_returns_null_no_token_written()
    {
        var (svc, ctx) = MakeService();
        var result = await svc.RequestLinkAsync("nobody@nowhere", "http://app", null, null, default);

        Assert.Null(result);
        await using var db = ctx();
        Assert.Empty(await db.MagicLinkTokens.ToListAsync());
    }

    [Fact]
    public async Task RequestLink_owner_creates_single_use_token_and_returns_dev_url()
    {
        var (svc, ctx) = MakeService(seedOwnerEmail: "me@example.com");
        var result = await svc.RequestLinkAsync("me@example.com", "http://app", "1.1.1.1", "ua", default);

        Assert.NotNull(result);
        Assert.NotNull(result!.DevMagicLinkUrl);
        Assert.Contains("/auth/callback?token=", result.DevMagicLinkUrl);

        await using var db = ctx();
        var tokens = await db.MagicLinkTokens.ToListAsync();
        var token = Assert.Single(tokens);
        Assert.Null(token.ConsumedAtUtc);
        Assert.True(token.ExpiresAtUtc > DateTime.UtcNow);
    }

    [Fact]
    public async Task RequestLink_twice_invalidates_first_token()
    {
        var (svc, ctx) = MakeService(seedOwnerEmail: "me@example.com");

        await svc.RequestLinkAsync("me@example.com", "http://app", null, null, default);
        await svc.RequestLinkAsync("me@example.com", "http://app", null, null, default);

        await using var db = ctx();
        var tokens = await db.MagicLinkTokens.OrderBy(t => t.IssuedAtUtc).ToListAsync();
        Assert.Equal(2, tokens.Count);
        // First one was marked consumed at request time so the new one is the only valid one.
        Assert.NotNull(tokens[0].ConsumedAtUtc);
        Assert.Null(tokens[1].ConsumedAtUtc);
    }

    [Fact]
    public async Task Consume_valid_token_creates_session()
    {
        var (svc, ctx) = MakeService(seedOwnerEmail: "me@example.com");
        var requestResult = await svc.RequestLinkAsync("me@example.com", "http://app", null, null, default);
        Assert.NotNull(requestResult);
        var rawToken = new Uri(requestResult!.DevMagicLinkUrl!).Query
            .TrimStart('?')
            .Split('=')[1];

        var session = await svc.ConsumeLinkAsync(Uri.UnescapeDataString(rawToken), null, null, default);
        Assert.NotNull(session);

        await using var db = ctx();
        var stored = await db.Sessions.FirstAsync();
        Assert.Equal(WellKnownUsers.OwnerId, stored.UserId);
        Assert.Null(stored.RevokedAtUtc);
        Assert.True(stored.ExpiresAtUtc > DateTime.UtcNow);
    }

    [Fact]
    public async Task Consume_reused_token_returns_null()
    {
        var (svc, _) = MakeService(seedOwnerEmail: "me@example.com");
        var result = await svc.RequestLinkAsync("me@example.com", "http://app", null, null, default);
        var rawToken = Uri.UnescapeDataString(new Uri(result!.DevMagicLinkUrl!).Query.TrimStart('?').Split('=')[1]);

        var first = await svc.ConsumeLinkAsync(rawToken, null, null, default);
        Assert.NotNull(first);

        var second = await svc.ConsumeLinkAsync(rawToken, null, null, default);
        Assert.Null(second);
    }

    [Fact]
    public async Task StartDemoSession_creates_session_for_demo_user()
    {
        var (svc, ctx) = MakeService();
        var session = await svc.StartDemoSessionAsync(null, null, default);

        Assert.NotNull(session);
        await using var db = ctx();
        var stored = await db.Sessions.FirstAsync();
        Assert.Equal(WellKnownUsers.DemoId, stored.UserId);
    }

    [Fact]
    public async Task Revoke_invalidates_session()
    {
        var (svc, ctx) = MakeService();
        var session = await svc.StartDemoSessionAsync(null, null, default);
        Assert.NotNull(session);

        await svc.RevokeAsync(session!.Id, allForUser: false, default);

        await using var db = ctx();
        var stored = await db.Sessions.FirstAsync();
        Assert.NotNull(stored.RevokedAtUtc);
    }

    // -- helpers --

    private static (IAuthService Service, Func<MusicHoarderDbContext> CreateCtx) MakeService(string? seedOwnerEmail = null)
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        // Seed Owner + Demo rows (HasData fires for relational providers only; InMemory needs an
        // explicit seed).
        using (var seed = new MusicHoarderDbContext(options))
        {
            seed.Users.AddRange(
                new User
                {
                    Id = WellKnownUsers.OwnerId,
                    Email = seedOwnerEmail ?? "owner@example.com",
                    EmailNormalized = User.Normalize(seedOwnerEmail ?? "owner@example.com"),
                    DisplayName = "Owner",
                    Role = UserRole.Owner,
                    CreatedAtUtc = DateTime.UtcNow,
                },
                new User
                {
                    Id = WellKnownUsers.DemoId,
                    Email = "demo@example.com",
                    EmailNormalized = User.Normalize("demo@example.com"),
                    DisplayName = "Demo",
                    Role = UserRole.Demo,
                    CreatedAtUtc = DateTime.UtcNow,
                });
            seed.SaveChanges();
        }

        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddScoped(sp => new MusicHoarderDbContext(sp.GetRequiredService<DbContextOptions<MusicHoarderDbContext>>()));
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var sender = new ConsoleMagicLinkSender(NullLogger<ConsoleMagicLinkSender>.Instance);
        var auth = new AuthService(
            scopeFactory,
            sender,
            new TestHostEnvironment("Development"),
            new TestOptionsMonitor<AuthOptions>(new AuthOptions
            {
                OwnerEmail = seedOwnerEmail ?? "owner@example.com",
                DemoUserEmail = "demo@example.com",
                MagicLinkTtlMinutes = 15,
                SessionLifetimeDays = 30,
            }),
            NullLogger<AuthService>.Instance);

        MusicHoarderDbContext CreateCtx() => new(options);
        return (auth, CreateCtx);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string env) => EnvironmentName = env;
        public string EnvironmentName { get; set; }
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
}
