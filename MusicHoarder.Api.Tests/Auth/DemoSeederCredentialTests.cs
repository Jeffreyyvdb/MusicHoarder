using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Auth;

public class DemoSeederCredentialTests
{
    private static readonly byte[] CredId = [1, 2, 3, 4];
    private static readonly byte[] PubKey = [9, 8, 7, 6, 5];

    private static string SeedJson(int signCount = 0) =>
        $$"""
        {"credentialId":"{{Convert.ToBase64String(CredId)}}","publicKey":"{{Convert.ToBase64String(PubKey)}}","aaGuid":"00000000-0000-0000-0000-000000000001","signCount":{{signCount}},"transports":"internal,hybrid","displayName":"Seed key"}
        """;

    [Fact]
    public async Task seeds_owner_passkey_into_empty_db()
    {
        var (svc, ctx) = MakeSeeder(new AuthOptions { OwnerSeedCredentialJson = SeedJson() });

        await svc.StartAsync(default);

        await using var db = ctx();
        var cred = await db.WebAuthnCredentials.SingleAsync();
        Assert.Equal(WellKnownUsers.OwnerId, cred.UserId);
        Assert.Equal(CredId, cred.CredentialId);
        Assert.Equal(PubKey, cred.PublicKey);
        Assert.Equal("internal,hybrid", cred.Transports);
        Assert.Equal("Seed key", cred.DisplayName);
    }

    [Fact]
    public async Task skips_when_owner_already_has_a_credential()
    {
        var (svc, ctx) = MakeSeeder(new AuthOptions { OwnerSeedCredentialJson = SeedJson() });
        await using (var pre = ctx())
        {
            pre.WebAuthnCredentials.Add(new WebAuthnCredential
            {
                Id = Guid.NewGuid(),
                UserId = WellKnownUsers.OwnerId,
                CredentialId = [42],
                PublicKey = [42],
                DisplayName = "Existing",
                CreatedAtUtc = DateTime.UtcNow,
            });
            await pre.SaveChangesAsync();
        }

        await svc.StartAsync(default);

        await using var db = ctx();
        var cred = await db.WebAuthnCredentials.SingleAsync();
        Assert.Equal("Existing", cred.DisplayName);
    }

    [Fact]
    public async Task does_nothing_when_blob_absent()
    {
        var (svc, ctx) = MakeSeeder(new AuthOptions { OwnerSeedCredentialJson = "" });

        await svc.StartAsync(default);

        await using var db = ctx();
        Assert.False(await db.WebAuthnCredentials.AnyAsync());
    }

    [Fact]
    public async Task malformed_blob_is_swallowed_and_seeds_nothing()
    {
        var (svc, ctx) = MakeSeeder(new AuthOptions { OwnerSeedCredentialJson = "{ not json" });

        await svc.StartAsync(default);

        await using var db = ctx();
        Assert.False(await db.WebAuthnCredentials.AnyAsync());
    }

    private static (DemoSeederHostedService Service, Func<MusicHoarderDbContext> CreateCtx) MakeSeeder(AuthOptions opts)
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        // HasData fires for relational providers only; InMemory needs the owner row seeded explicitly.
        using (var seed = new MusicHoarderDbContext(options))
        {
            seed.Users.Add(new User
            {
                Id = WellKnownUsers.OwnerId,
                Email = "owner@example.com",
                EmailNormalized = User.Normalize("owner@example.com"),
                DisplayName = "Owner",
                Role = UserRole.Owner,
                CreatedAtUtc = DateTime.UtcNow,
            });
            seed.SaveChanges();
        }

        // The seeder updates the owner email from AuthOptions, which requires a valid address.
        opts.OwnerEmail = "owner@example.com";

        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddScoped(sp => new MusicHoarderDbContext(sp.GetRequiredService<DbContextOptions<MusicHoarderDbContext>>()));
        var sp = services.BuildServiceProvider();

        var svc = new DemoSeederHostedService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new TestOptionsMonitor<AuthOptions>(opts),
            NullLogger<DemoSeederHostedService>.Instance);

        return (svc, () => new MusicHoarderDbContext(options));
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T> where T : class
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
