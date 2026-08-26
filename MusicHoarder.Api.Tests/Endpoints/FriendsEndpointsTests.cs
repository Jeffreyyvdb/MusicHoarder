using Microsoft.AspNetCore.Http;
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
/// Owner-side friend management: invite create-or-rotate through the endpoint (URL shape, owner/demo
/// rejection), grant create idempotence + key normalization, and remove-friend's full teardown
/// (disable + session revocation + grant revocation) that never touches Owner/Demo rows.
/// </summary>
public class FriendsEndpointsTests
{
    [Fact]
    public async Task CreateInvite_returns_frontend_invite_url_and_rotates()
    {
        var (options, auth) = Setup();
        await using var db = OwnerContext(options);

        var first = Value(await FriendsEndpoints.CreateInvite(
            new FriendsEndpoints.CreateInviteRequest("pal@example.com", SendEmail: null),
            new DefaultHttpContext(), db, OwnerAccessor(), auth, ConsoleSender(), Frontend(), CancellationToken.None));

        var url = GetProperty<string>(first, "InviteUrl");
        Assert.StartsWith("https://app.test/invite/", url);
        Assert.False(GetProperty<bool>(first, "EmailSent"));

        var second = Value(await FriendsEndpoints.CreateInvite(
            new FriendsEndpoints.CreateInviteRequest("pal@example.com", SendEmail: null),
            new DefaultHttpContext(), db, OwnerAccessor(), auth, ConsoleSender(), Frontend(), CancellationToken.None));

        Assert.Equal(GetProperty<Guid>(first, "Id"), GetProperty<Guid>(second, "Id"));
        Assert.NotEqual(url, GetProperty<string>(second, "InviteUrl")); // rotated token, same row

        await using var verify = new MusicHoarderDbContext(options);
        Assert.Single(await verify.Invites.ToListAsync());
    }

    [Theory]
    [InlineData("owner@example.com")]
    [InlineData("demo@example.com")]
    public async Task CreateInvite_for_existing_owner_or_demo_email_is_400(string email)
    {
        var (options, auth) = Setup();
        await using var db = OwnerContext(options);

        var result = await FriendsEndpoints.CreateInvite(
            new FriendsEndpoints.CreateInviteRequest(email, SendEmail: null),
            new DefaultHttpContext(), db, OwnerAccessor(), auth, ConsoleSender(), Frontend(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task ListInvites_shows_only_active_ones()
    {
        var (options, auth) = Setup();
        await using var db = OwnerContext(options);
        var ctx = new DefaultHttpContext();

        await FriendsEndpoints.CreateInvite(new FriendsEndpoints.CreateInviteRequest("a@example.com", null),
            ctx, db, OwnerAccessor(), auth, ConsoleSender(), Frontend(), CancellationToken.None);
        var revokable = Value(await FriendsEndpoints.CreateInvite(new FriendsEndpoints.CreateInviteRequest("b@example.com", null),
            ctx, db, OwnerAccessor(), auth, ConsoleSender(), Frontend(), CancellationToken.None));

        var revokeResult = await FriendsEndpoints.RevokeInvite(GetProperty<Guid>(revokable, "Id"), db, CancellationToken.None);
        Assert.Equal(StatusCodes.Status204NoContent, ((IStatusCodeHttpResult)revokeResult).StatusCode);

        var list = (System.Collections.IEnumerable)Value(await FriendsEndpoints.ListInvites(db, CancellationToken.None));
        var item = Assert.Single(list.Cast<object>());
        Assert.Equal("a@example.com", GetProperty<string>(item, "Email"));
    }

    [Fact]
    public async Task CreateGrant_normalizes_keys_and_is_idempotent()
    {
        var (options, _) = Setup(seedFriend: true);
        await using var db = OwnerContext(options);

        var first = Value(await FriendsEndpoints.CreateGrant(
            TestUsers.FriendId,
            new FriendsEndpoints.CreateGrantRequest("album", "  Daft Punk ", "Discovery"),
            db, OwnerAccessor(), CancellationToken.None));
        var second = Value(await FriendsEndpoints.CreateGrant(
            TestUsers.FriendId,
            new FriendsEndpoints.CreateGrantRequest("Album", "DAFT PUNK", "DISCOVERY"),
            db, OwnerAccessor(), CancellationToken.None));

        Assert.Equal(GetProperty<int>(first, "Id"), GetProperty<int>(second, "Id"));

        await using var verify = new MusicHoarderDbContext(options);
        var grant = Assert.Single(await verify.LibraryShareGrants.ToListAsync());
        Assert.Equal("daft punk", grant.ArtistKey);
        Assert.Equal("discovery", grant.AlbumKey);
        Assert.Equal("Daft Punk", grant.ArtistDisplay);
        Assert.Equal("Discovery", grant.AlbumDisplay);
    }

    [Theory]
    [InlineData("bogus", "A", "X")]
    [InlineData("album", null, "X")]
    [InlineData("album", "A", null)]
    [InlineData("artist", null, null)]
    public async Task CreateGrant_validates_scope_and_required_fields(string scope, string? artist, string? album)
    {
        var (options, _) = Setup(seedFriend: true);
        await using var db = OwnerContext(options);

        var result = await FriendsEndpoints.CreateGrant(
            TestUsers.FriendId,
            new FriendsEndpoints.CreateGrantRequest(scope, artist, album),
            db, OwnerAccessor(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task CreateGrant_for_owner_or_demo_id_is_404()
    {
        var (options, _) = Setup(seedFriend: true);
        await using var db = OwnerContext(options);

        foreach (var id in new[] { TestUsers.OwnerId, TestUsers.DemoId, Guid.NewGuid() })
        {
            var result = await FriendsEndpoints.CreateGrant(
                id, new FriendsEndpoints.CreateGrantRequest("library", null, null),
                db, OwnerAccessor(), CancellationToken.None);
            Assert.Equal(StatusCodes.Status404NotFound, ((IStatusCodeHttpResult)result).StatusCode);
        }
    }

    [Fact]
    public async Task RemoveFriend_disables_kills_sessions_and_revokes_grants()
    {
        var (options, _) = Setup(seedFriend: true);
        await using (var seed = new MusicHoarderDbContext(options))
        {
            seed.Sessions.Add(new Session
            {
                Id = Guid.NewGuid(),
                UserId = TestUsers.FriendId,
                IssuedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddDays(30),
            });
            seed.LibraryShareGrants.Add(new LibraryShareGrant
            {
                OwnerUserId = TestUsers.OwnerId,
                GranteeUserId = TestUsers.FriendId,
                Scope = ShareGrantScope.Library,
                CreatedAtUtc = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using var db = OwnerContext(options);
        var result = await FriendsEndpoints.RemoveFriend(TestUsers.FriendId, db, OwnerAccessor(), CancellationToken.None);
        Assert.Equal(StatusCodes.Status204NoContent, ((IStatusCodeHttpResult)result).StatusCode);

        await using var verify = new MusicHoarderDbContext(options);
        Assert.True((await verify.Users.SingleAsync(u => u.Id == TestUsers.FriendId)).IsDisabled);
        Assert.NotNull((await verify.Sessions.SingleAsync()).RevokedAtUtc);
        Assert.NotNull((await verify.LibraryShareGrants.SingleAsync()).RevokedAtUtc);
    }

    [Fact]
    public async Task RemoveFriend_never_touches_owner_or_demo()
    {
        var (options, _) = Setup();
        await using var db = OwnerContext(options);

        foreach (var id in new[] { TestUsers.OwnerId, TestUsers.DemoId })
        {
            var result = await FriendsEndpoints.RemoveFriend(id, db, OwnerAccessor(), CancellationToken.None);
            Assert.Equal(StatusCodes.Status404NotFound, ((IStatusCodeHttpResult)result).StatusCode);
        }

        await using var verify = new MusicHoarderDbContext(options);
        Assert.All(await verify.Users.ToListAsync(), u => Assert.False(u.IsDisabled));
    }

    [Fact]
    public async Task ListFriends_includes_grants()
    {
        var (options, _) = Setup(seedFriend: true);
        await using (var seed = new MusicHoarderDbContext(options))
        {
            seed.LibraryShareGrants.Add(new LibraryShareGrant
            {
                OwnerUserId = TestUsers.OwnerId,
                GranteeUserId = TestUsers.FriendId,
                Scope = ShareGrantScope.Album,
                ArtistKey = "daft punk",
                AlbumKey = "discovery",
                ArtistDisplay = "Daft Punk",
                AlbumDisplay = "Discovery",
                CreatedAtUtc = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using var db = OwnerContext(options);
        var list = ((System.Collections.IEnumerable)Value(
            await FriendsEndpoints.ListFriends(db, OwnerAccessor(), CancellationToken.None))).Cast<object>().ToList();

        var friend = Assert.Single(list);
        Assert.Equal("friend@test.local", GetProperty<string>(friend, "Email"));
        var grants = ((System.Collections.IEnumerable)GetProperty<object>(friend, "Grants")).Cast<object>().ToList();
        var grant = Assert.Single(grants);
        Assert.Equal("Album", GetProperty<string>(grant, "Scope"));
        Assert.Equal("Daft Punk", GetProperty<string?>(grant, "Artist"));
    }

    // -- helpers --

    private static (DbContextOptions<MusicHoarderDbContext> Options, IAuthService Auth) Setup(bool seedFriend = false)
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
                });
            if (seedFriend)
            {
                seed.Users.Add(new User
                {
                    Id = TestUsers.FriendId,
                    Email = "friend@test.local",
                    EmailNormalized = User.Normalize("friend@test.local"),
                    Role = UserRole.Member,
                    CreatedAtUtc = DateTime.UtcNow,
                });
            }
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

    private static MusicHoarderDbContext OwnerContext(DbContextOptions<MusicHoarderDbContext> options) =>
        new(options, new TestCurrentUserAccessor(TestCurrentUserAccessor.OwnerUser));

    private static TestCurrentUserAccessor OwnerAccessor() =>
        new(TestCurrentUserAccessor.OwnerUser);

    private static ConsoleMagicLinkSender ConsoleSender() =>
        new(NullLogger<ConsoleMagicLinkSender>.Instance);

    private static IOptions<FrontendOptions> Frontend() =>
        Microsoft.Extensions.Options.Options.Create(new FrontendOptions { PublicBaseUrl = "https://app.test" });

    private static object Value(IResult result)
        => result.GetType().GetProperty("Value")!.GetValue(result)!;

    private static T GetProperty<T>(object obj, string name)
    {
        var prop = obj.GetType().GetProperty(name);
        Assert.NotNull(prop);
        return (T)prop!.GetValue(obj)!;
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
