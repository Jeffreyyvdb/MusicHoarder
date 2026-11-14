using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Endpoints;

/// <summary>
/// Removing a member revokes the invites still pending for that member's email, so an
/// earlier invite link cannot bring the account back.
/// </summary>
public class RemoveFriendRevokesInvitesTests
{
    [Fact]
    public async Task Removed_member_cannot_accept_invite_minted_before_removal()
    {
        var (options, auth) = Setup();
        var minted = await auth.CreateOrRotateInviteAsync(TestUsers.OwnerId, "friend@test.local", CancellationToken.None);
        Assert.NotNull(minted);

        await using (var db = new MusicHoarderDbContext(options, new TestCurrentUserAccessor(TestCurrentUserAccessor.OwnerUser)))
        {
            await FriendsEndpoints.RemoveFriend(
                TestUsers.FriendId, db, new TestCurrentUserAccessor(TestCurrentUserAccessor.OwnerUser), CancellationToken.None);
        }

        var session = await auth.AcceptInviteAsync(minted!.RawToken, null, null, CancellationToken.None);
        Assert.Null(session);

        await using var verify = new MusicHoarderDbContext(options);
        Assert.True((await verify.Users.SingleAsync(u => u.Id == TestUsers.FriendId)).IsDisabled);
    }

    [Fact]
    public async Task Invite_minted_after_removal_still_lets_member_back_in()
    {
        var (options, auth) = Setup();
        await using (var db = new MusicHoarderDbContext(options, new TestCurrentUserAccessor(TestCurrentUserAccessor.OwnerUser)))
        {
            await FriendsEndpoints.RemoveFriend(
                TestUsers.FriendId, db, new TestCurrentUserAccessor(TestCurrentUserAccessor.OwnerUser), CancellationToken.None);
        }

        var minted = await auth.CreateOrRotateInviteAsync(TestUsers.OwnerId, "friend@test.local", CancellationToken.None);
        var session = await auth.AcceptInviteAsync(minted!.RawToken, null, null, CancellationToken.None);
        Assert.NotNull(session);
    }

    private static (DbContextOptions<MusicHoarderDbContext> Options, IAuthService Auth) Setup()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using (var seed = new MusicHoarderDbContext(options))
        {
            seed.Users.AddRange(
                new User
                {
                    Id = TestUsers.OwnerId,
                    Email = "owner@example.com",
                    EmailNormalized = User.Normalize("owner@example.com"),
                    DisplayName = "Owner",
                    Role = UserRole.Admin,
                    CreatedAtUtc = DateTime.UtcNow,
                },
                new User
                {
                    Id = TestUsers.DemoId,
                    Email = "demo@example.com",
                    EmailNormalized = User.Normalize("demo@example.com"),
                    DisplayName = "Demo",
                    Role = UserRole.Demo,
                    CreatedAtUtc = DateTime.UtcNow,
                },
                new User
                {
                    Id = TestUsers.FriendId,
                    Email = "friend@test.local",
                    EmailNormalized = User.Normalize("friend@test.local"),
                    Role = UserRole.Member,
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
            }),
            NullLogger<AuthService>.Instance);

        return (options, auth);
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
