using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Auth;

public class RevokeAllClearsPushSubscriptionsTests
{
    [Fact]
    public async Task Signing_out_everywhere_removes_that_accounts_push_subscriptions_only()
    {
        var (svc, ctx) = MakeService();
        var session = await svc.StartDemoSessionAsync(null, null, default);
        Assert.NotNull(session);

        await using (var seed = ctx())
        {
            seed.WebPushSubscriptions.AddRange(
                NewSub(WellKnownUsers.DemoId, "https://push.example.com/demo-1"),
                NewSub(WellKnownUsers.DemoId, "https://push.example.com/demo-2"),
                NewSub(WellKnownUsers.OwnerId, "https://push.example.com/owner-1"));
            await seed.SaveChangesAsync();
        }

        await svc.RevokeAsync(session!.Id, allForUser: true, default);

        await using var db = ctx();
        var remaining = await db.WebPushSubscriptions.IgnoreQueryFilters().ToListAsync();
        var kept = Assert.Single(remaining);
        Assert.Equal(WellKnownUsers.OwnerId, kept.UserId);
    }

    private static WebPushSubscription NewSub(Guid userId, string endpoint) => new()
    {
        UserId = userId,
        Endpoint = endpoint,
        P256dh = "p256dh-dummy",
        Auth = "auth-dummy",
        CreatedAtUtc = DateTime.UtcNow,
    };

    private static (IAuthService Service, Func<MusicHoarderDbContext> CreateCtx) MakeService()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using (var seed = new MusicHoarderDbContext(options))
        {
            seed.Users.AddRange(
                new User
                {
                    Id = WellKnownUsers.OwnerId,
                    Email = "owner@example.com",
                    EmailNormalized = User.Normalize("owner@example.com"),
                    DisplayName = "Owner",
                    Role = UserRole.Admin,
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

        var auth = new AuthService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new ConsoleMagicLinkSender(NullLogger<ConsoleMagicLinkSender>.Instance),
            new TestHostEnvironment(),
            new TestOptionsMonitor<AuthOptions>(new AuthOptions
            {
                OwnerEmail = "owner@example.com",
                DemoUserEmail = "demo@example.com",
                MagicLinkTtlMinutes = 15,
                SessionLifetimeDays = 30,
            }),
            NullLogger<AuthService>.Instance);

        return (auth, () => new MusicHoarderDbContext(options));
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
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
