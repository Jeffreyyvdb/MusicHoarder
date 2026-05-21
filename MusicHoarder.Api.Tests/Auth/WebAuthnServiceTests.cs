using Fido2NetLib;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Auth;

public class WebAuthnServiceTests
{
    [Fact]
    public async Task BeginRegistration_returns_options_for_owner()
    {
        var (svc, _) = MakeService();
        var options = await svc.BeginRegistrationAsync(WellKnownUsers.OwnerId, default);

        Assert.NotNull(options);
        Assert.NotNull(options!.Challenge);
        Assert.NotEmpty(options.Challenge);
        Assert.Equal(WellKnownUsers.OwnerId.ToByteArray(), options.User.Id);
    }

    [Fact]
    public async Task BeginRegistration_returns_null_for_unknown_user()
    {
        var (svc, _) = MakeService();
        var options = await svc.BeginRegistrationAsync(Guid.NewGuid(), default);

        Assert.Null(options);
    }

    [Fact]
    public void BeginAuthentication_issues_challenge_with_empty_allow_list()
    {
        var (svc, _) = MakeService();
        var options = svc.BeginAuthentication();

        Assert.NotNull(options.Challenge);
        Assert.NotEmpty(options.Challenge);
        Assert.True(options.AllowCredentials is null || options.AllowCredentials.Count == 0);
    }

    [Fact]
    public async Task List_returns_only_the_users_credentials_newest_first()
    {
        var (svc, ctx) = MakeService();
        await using (var db = ctx())
        {
            db.WebAuthnCredentials.AddRange(
                NewCredential(WellKnownUsers.OwnerId, "old", DateTime.UtcNow.AddDays(-2)),
                NewCredential(WellKnownUsers.OwnerId, "new", DateTime.UtcNow),
                NewCredential(WellKnownUsers.DemoId, "demo", DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        var owned = await svc.ListCredentialsAsync(WellKnownUsers.OwnerId, default);

        Assert.Equal(2, owned.Count);
        Assert.Equal("new", owned[0].DisplayName);
        Assert.Equal("old", owned[1].DisplayName);
    }

    [Fact]
    public async Task Delete_removes_only_own_credential()
    {
        var (svc, ctx) = MakeService();
        var ownerCred = NewCredential(WellKnownUsers.OwnerId, "owner", DateTime.UtcNow);
        var demoCred = NewCredential(WellKnownUsers.DemoId, "demo", DateTime.UtcNow);
        await using (var db = ctx())
        {
            db.WebAuthnCredentials.AddRange(ownerCred, demoCred);
            await db.SaveChangesAsync();
        }

        // Owner cannot delete the demo user's credential.
        Assert.False(await svc.DeleteCredentialAsync(WellKnownUsers.OwnerId, demoCred.Id, default));
        // Owner can delete their own.
        Assert.True(await svc.DeleteCredentialAsync(WellKnownUsers.OwnerId, ownerCred.Id, default));

        await using var verify = ctx();
        Assert.Null(await verify.WebAuthnCredentials.FindAsync(ownerCred.Id));
        Assert.NotNull(await verify.WebAuthnCredentials.FindAsync(demoCred.Id));
    }

    [Fact]
    public async Task CompleteAuthentication_returns_null_for_unknown_credential()
    {
        var (svc, _) = MakeService();
        var assertion = new AuthenticatorAssertionRawResponse { RawId = [9, 9, 9], Id = "unknown" };

        var session = await svc.CompleteAuthenticationAsync(
            assertion, svc.BeginAuthentication(), null, null, default);

        Assert.Null(session);
    }

    // -- helpers --

    private static WebAuthnCredential NewCredential(Guid userId, string name, DateTime createdAtUtc) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CredentialId = Guid.NewGuid().ToByteArray(),
            PublicKey = [1, 2, 3],
            SignCount = 0,
            AaGuid = Guid.Empty,
            DisplayName = name,
            CreatedAtUtc = createdAtUtc,
        };

    private static (IWebAuthnService Service, Func<MusicHoarderDbContext> CreateCtx) MakeService()
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

        var fido2 = new Fido2(new Fido2Configuration
        {
            ServerDomain = "localhost",
            ServerName = "MusicHoarder Tests",
            Origins = new HashSet<string> { "https://localhost" },
        });

        var svc = new WebAuthnService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            fido2,
            new TestOptionsMonitor<AuthOptions>(new AuthOptions { SessionLifetimeDays = 30 }),
            NullLogger<WebAuthnService>.Instance);

        MusicHoarderDbContext CreateCtx() => new(options);
        return (svc, CreateCtx);
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T> where T : class
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
