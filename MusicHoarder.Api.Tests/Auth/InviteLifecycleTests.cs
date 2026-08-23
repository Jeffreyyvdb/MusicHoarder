using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Auth;

/// <summary>
/// The friend-invite lifecycle on <see cref="AuthService"/>: mint/rotate (hash-only storage),
/// peek without consuming, and accept (the one runtime path that inserts a
/// <see cref="UserRole.Friend"/> user). Uniform failure for expired/revoked/consumed tokens.
/// </summary>
public class InviteLifecycleTests
{
    [Fact]
    public async Task Create_stores_hash_only_and_returns_raw_token()
    {
        var (svc, ctx) = MakeService();

        var minted = await svc.CreateOrRotateInviteAsync(TestUsers.OwnerId, "pal@example.com", default);

        Assert.NotNull(minted);
        Assert.False(string.IsNullOrWhiteSpace(minted!.RawToken));
        await using var db = ctx();
        var invite = Assert.Single(await db.Invites.ToListAsync());
        Assert.Equal("pal@example.com", invite.Email);
        Assert.Equal(User.Normalize("pal@example.com"), invite.EmailNormalized);
        Assert.Equal(AuthService.Sha256(minted.RawToken), invite.TokenHash);
        Assert.Null(invite.ConsumedAtUtc);
        Assert.True(invite.ExpiresAtUtc > DateTime.UtcNow);
    }

    [Fact]
    public async Task Create_again_rotates_token_in_place_and_kills_old_link()
    {
        var (svc, ctx) = MakeService();

        var first = await svc.CreateOrRotateInviteAsync(TestUsers.OwnerId, "pal@example.com", default);
        var second = await svc.CreateOrRotateInviteAsync(TestUsers.OwnerId, "PAL@example.com", default);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.RawToken, second!.RawToken);
        Assert.Equal(first.Invite.Id, second.Invite.Id); // same row, rotated

        await using var db = ctx();
        Assert.Single(await db.Invites.ToListAsync());
        Assert.Null(await svc.PeekInviteAsync(first.RawToken, default));
        Assert.NotNull(await svc.PeekInviteAsync(second.RawToken, default));
    }

    [Theory]
    [InlineData("owner@example.com")]
    [InlineData("demo@example.com")]
    public async Task Create_for_owner_or_demo_email_is_rejected(string email)
    {
        var (svc, ctx) = MakeService();

        var minted = await svc.CreateOrRotateInviteAsync(TestUsers.OwnerId, email, default);

        Assert.Null(minted);
        await using var db = ctx();
        Assert.Empty(await db.Invites.ToListAsync());
    }

    [Fact]
    public async Task Peek_returns_inviter_and_email_without_consuming()
    {
        var (svc, ctx) = MakeService();
        var minted = await svc.CreateOrRotateInviteAsync(TestUsers.OwnerId, "pal@example.com", default);

        var peek = await svc.PeekInviteAsync(minted!.RawToken, default);

        Assert.NotNull(peek);
        Assert.Equal("Owner", peek!.InviterName);
        Assert.Equal("pal@example.com", peek.Email);
        await using var db = ctx();
        Assert.Null((await db.Invites.SingleAsync()).ConsumedAtUtc);
    }

    [Fact]
    public async Task Accept_creates_friend_user_session_and_consumes()
    {
        var (svc, ctx) = MakeService();
        var minted = await svc.CreateOrRotateInviteAsync(TestUsers.OwnerId, "pal@example.com", default);

        var session = await svc.AcceptInviteAsync(minted!.RawToken, "1.2.3.4", "browser", default);

        Assert.NotNull(session);
        await using var db = ctx();
        var friend = await db.Users.SingleAsync(u => u.EmailNormalized == User.Normalize("pal@example.com"));
        Assert.Equal(UserRole.Friend, friend.Role);
        Assert.False(friend.IsDisabled);
        Assert.NotNull(friend.LastLoginAtUtc);

        var invite = await db.Invites.SingleAsync();
        Assert.NotNull(invite.ConsumedAtUtc);
        Assert.Equal(friend.Id, invite.ConsumedByUserId);

        var stored = await db.Sessions.SingleAsync();
        Assert.Equal(friend.Id, stored.UserId);
    }

    [Fact]
    public async Task Accept_twice_fails_second_time()
    {
        var (svc, _) = MakeService();
        var minted = await svc.CreateOrRotateInviteAsync(TestUsers.OwnerId, "pal@example.com", default);

        Assert.NotNull(await svc.AcceptInviteAsync(minted!.RawToken, null, null, default));
        Assert.Null(await svc.AcceptInviteAsync(minted.RawToken, null, null, default));
    }

    [Fact]
    public async Task Accept_unknown_revoked_or_expired_fails_uniformly()
    {
        var (svc, ctx) = MakeService();

        Assert.Null(await svc.AcceptInviteAsync("not-a-token", null, null, default));

        var revoked = await svc.CreateOrRotateInviteAsync(TestUsers.OwnerId, "revoked@example.com", default);
        await using (var db = ctx())
        {
            var row = await db.Invites.SingleAsync(i => i.Id == revoked!.Invite.Id);
            row.RevokedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        Assert.Null(await svc.AcceptInviteAsync(revoked!.RawToken, null, null, default));
        Assert.Null(await svc.PeekInviteAsync(revoked.RawToken, default));

        var expired = await svc.CreateOrRotateInviteAsync(TestUsers.OwnerId, "late@example.com", default);
        await using (var db = ctx())
        {
            var row = await db.Invites.SingleAsync(i => i.Id == expired!.Invite.Id);
            row.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        Assert.Null(await svc.AcceptInviteAsync(expired!.RawToken, null, null, default));
        Assert.Null(await svc.PeekInviteAsync(expired.RawToken, default));
    }

    [Fact]
    public async Task Accept_reenables_a_removed_friend_with_same_email()
    {
        var (svc, ctx) = MakeService();

        // First invite → friend exists, then the owner removes (disables) them.
        var first = await svc.CreateOrRotateInviteAsync(TestUsers.OwnerId, "pal@example.com", default);
        await svc.AcceptInviteAsync(first!.RawToken, null, null, default);
        Guid friendId;
        await using (var db = ctx())
        {
            var friend = await db.Users.SingleAsync(u => u.Role == UserRole.Friend);
            friend.IsDisabled = true;
            friendId = friend.Id;
            await db.SaveChangesAsync();
        }

        // A fresh invite for the same email re-enables the same account instead of dead-ending.
        var second = await svc.CreateOrRotateInviteAsync(TestUsers.OwnerId, "pal@example.com", default);
        var session = await svc.AcceptInviteAsync(second!.RawToken, null, null, default);

        Assert.NotNull(session);
        await using var verify = ctx();
        var restored = await verify.Users.SingleAsync(u => u.Id == friendId);
        Assert.False(restored.IsDisabled);
        Assert.Equal(session!.UserId, friendId);
    }

    [Fact]
    public async Task Disabled_friend_session_is_rejected_by_ResolveSession()
    {
        var (svc, ctx) = MakeService();
        var minted = await svc.CreateOrRotateInviteAsync(TestUsers.OwnerId, "pal@example.com", default);
        var session = await svc.AcceptInviteAsync(minted!.RawToken, null, null, default);
        Assert.NotNull(await svc.ResolveSessionAsync(session!.Id, default));

        await using (var db = ctx())
        {
            var friend = await db.Users.SingleAsync(u => u.Role == UserRole.Friend);
            friend.IsDisabled = true;
            await db.SaveChangesAsync();
        }

        Assert.Null(await svc.ResolveSessionAsync(session.Id, default));
    }

    [Fact]
    public async Task Friend_can_request_magic_link_after_acceptance()
    {
        var (svc, ctx) = MakeService();
        var minted = await svc.CreateOrRotateInviteAsync(TestUsers.OwnerId, "pal@example.com", default);
        await svc.AcceptInviteAsync(minted!.RawToken, null, null, default);

        var link = await svc.RequestLinkAsync("pal@example.com", "http://app", null, null, default);

        Assert.NotNull(link);
        await using var db = ctx();
        Assert.Single(await db.MagicLinkTokens.ToListAsync());
    }

    // -- helpers --

    private static (IAuthService Service, Func<MusicHoarderDbContext> CreateCtx) MakeService()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(dbName)
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
}
