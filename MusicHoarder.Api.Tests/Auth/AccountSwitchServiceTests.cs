using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Auth;

/// <summary>
/// The multi-account switcher over the two cookies: <c>mh_session</c> (active) and
/// <c>mh_session_alts</c> (parked, capped, possession-is-credential). Uses the real
/// <see cref="SessionCookieService"/> over ephemeral data protection and the real
/// <see cref="AuthService"/> over EF InMemory, round-tripping response Set-Cookie headers into
/// the next request like a browser would.
/// </summary>
public class AccountSwitchServiceTests
{
    [Fact]
    public async Task first_sign_in_writes_active_cookie_only()
    {
        var h = new Harness();
        var jar = new CookieJar();

        await h.SignInAsync(jar, await h.MintSessionAsync(TestUsers.OwnerId));

        Assert.NotNull(jar.Get(h.Cookies.CookieName));
        Assert.Null(jar.Get(h.Cookies.AltsCookieName));
    }

    [Fact]
    public async Task sign_in_as_second_user_parks_the_first()
    {
        var h = new Harness();
        var jar = new CookieJar();
        await h.SignInAsync(jar, await h.MintSessionAsync(TestUsers.OwnerId));
        await h.SignInAsync(jar, await h.MintSessionAsync(TestUsers.FriendId));

        Assert.NotNull(jar.Get(h.Cookies.AltsCookieName));

        var accounts = await h.ListAsync(jar);
        Assert.Equal(2, accounts.Count);
        Assert.True(accounts[0].IsActive);
        Assert.Equal(TestUsers.FriendId, accounts[0].UserId);
        Assert.False(accounts[1].IsActive);
        Assert.Equal(TestUsers.OwnerId, accounts[1].UserId);
    }

    [Fact]
    public async Task same_user_re_login_replaces_active_and_never_parks_itself()
    {
        var h = new Harness();
        var jar = new CookieJar();
        await h.SignInAsync(jar, await h.MintSessionAsync(TestUsers.OwnerId));
        await h.SignInAsync(jar, await h.MintSessionAsync(TestUsers.FriendId));
        await h.SignInAsync(jar, await h.MintSessionAsync(TestUsers.FriendId));

        var accounts = await h.ListAsync(jar);
        Assert.Equal(2, accounts.Count);
        Assert.Equal(TestUsers.FriendId, accounts[0].UserId);
        Assert.Equal(TestUsers.OwnerId, accounts[1].UserId);
    }

    [Fact]
    public async Task parked_list_is_capped_at_four_dropping_the_oldest()
    {
        var h = new Harness();
        // 6 distinct users signing in one after another → 5 candidates to park, cap keeps 4.
        var users = new List<Guid> { TestUsers.OwnerId, TestUsers.DemoId, TestUsers.FriendId, TestUsers.SecondFriendId };
        users.AddRange(h.SeedExtraFriends(2));

        var jar = new CookieJar();
        foreach (var userId in users)
            await h.SignInAsync(jar, await h.MintSessionAsync(userId));

        var accounts = await h.ListAsync(jar);
        Assert.Equal(1 + AccountSwitchService.MaxParkedSessions, accounts.Count);
        Assert.Equal(users[^1], accounts[0].UserId);
        // The first user signed in (the oldest parked) fell off the end.
        Assert.DoesNotContain(accounts, a => a.UserId == users[0]);
    }

    [Fact]
    public async Task list_prunes_a_revoked_parked_session_and_rewrites_the_cookie()
    {
        var h = new Harness();
        var jar = new CookieJar();
        var ownerSession = await h.MintSessionAsync(TestUsers.OwnerId);
        await h.SignInAsync(jar, ownerSession);
        await h.SignInAsync(jar, await h.MintSessionAsync(TestUsers.FriendId));

        await h.Auth.RevokeAsync(ownerSession.Id, allForUser: false, default);

        var accounts = await h.ListAsync(jar);
        var only = Assert.Single(accounts);
        Assert.Equal(TestUsers.FriendId, only.UserId);
        Assert.Null(jar.Get(h.Cookies.AltsCookieName));
    }

    [Fact]
    public async Task switch_to_parked_user_swaps_active_and_parked()
    {
        var h = new Harness();
        var jar = new CookieJar();
        await h.SignInAsync(jar, await h.MintSessionAsync(TestUsers.OwnerId));
        await h.SignInAsync(jar, await h.MintSessionAsync(TestUsers.FriendId));

        var switched = await h.SwitchAsync(jar, TestUsers.OwnerId);

        Assert.NotNull(switched);
        Assert.Equal(TestUsers.OwnerId, switched!.UserId);
        Assert.True(switched.IsActive);

        var accounts = await h.ListAsync(jar);
        Assert.Equal(TestUsers.OwnerId, accounts[0].UserId);
        Assert.Equal(TestUsers.FriendId, accounts[1].UserId);
    }

    [Fact]
    public async Task switch_to_user_not_in_alts_returns_null_and_never_mints_a_session()
    {
        var h = new Harness();
        var jar = new CookieJar();
        await h.SignInAsync(jar, await h.MintSessionAsync(TestUsers.OwnerId));
        await h.SignInAsync(jar, await h.MintSessionAsync(TestUsers.FriendId));

        await using var before = h.CreateCtx();
        var countBefore = await before.Sessions.CountAsync();

        // SecondFriend is a perfectly valid user in the DB — but not in this browser's cookies.
        var switched = await h.SwitchAsync(jar, TestUsers.SecondFriendId);

        Assert.Null(switched);
        await using var after = h.CreateCtx();
        Assert.Equal(countBefore, await after.Sessions.CountAsync());
    }

    [Fact]
    public async Task switch_to_revoked_parked_session_returns_null_and_prunes_it()
    {
        var h = new Harness();
        var jar = new CookieJar();
        var ownerSession = await h.MintSessionAsync(TestUsers.OwnerId);
        await h.SignInAsync(jar, ownerSession);
        await h.SignInAsync(jar, await h.MintSessionAsync(TestUsers.FriendId));
        await h.Auth.RevokeAsync(ownerSession.Id, allForUser: false, default);

        var switched = await h.SwitchAsync(jar, TestUsers.OwnerId);

        Assert.Null(switched);
        Assert.Null(jar.Get(h.Cookies.AltsCookieName));
    }

    [Fact]
    public async Task logout_single_promotes_the_parked_account()
    {
        var h = new Harness();
        var jar = new CookieJar();
        await h.SignInAsync(jar, await h.MintSessionAsync(TestUsers.OwnerId));
        var friendSession = await h.MintSessionAsync(TestUsers.FriendId);
        await h.SignInAsync(jar, friendSession);

        var outcome = await h.LogoutAsync(jar, all: false);

        Assert.NotNull(outcome.Fallback);
        Assert.Equal(TestUsers.OwnerId, outcome.Fallback!.UserId);

        // The friend's session is revoked; the promoted owner session is the active cookie.
        await using var db = h.CreateCtx();
        var revoked = await db.Sessions.FirstAsync(s => s.Id == friendSession.Id);
        Assert.NotNull(revoked.RevokedAtUtc);

        var accounts = await h.ListAsync(jar);
        var only = Assert.Single(accounts);
        Assert.Equal(TestUsers.OwnerId, only.UserId);
    }

    [Fact]
    public async Task logout_single_with_no_parked_clears_both_cookies()
    {
        var h = new Harness();
        var jar = new CookieJar();
        await h.SignInAsync(jar, await h.MintSessionAsync(TestUsers.OwnerId));

        var outcome = await h.LogoutAsync(jar, all: false);

        Assert.Null(outcome.Fallback);
        Assert.Null(jar.Get(h.Cookies.CookieName));
        Assert.Null(jar.Get(h.Cookies.AltsCookieName));
    }

    [Fact]
    public async Task logout_everywhere_revokes_only_the_active_users_sessions()
    {
        var h = new Harness();
        var jar = new CookieJar();
        var ownerParked = await h.MintSessionAsync(TestUsers.OwnerId);
        await h.SignInAsync(jar, ownerParked);
        var friendPhone = await h.MintSessionAsync(TestUsers.FriendId); // e.g. a paired device
        await h.SignInAsync(jar, await h.MintSessionAsync(TestUsers.FriendId));

        var outcome = await h.LogoutAsync(jar, all: true);

        // No silent fallback after a security action; the browser lands signed out.
        Assert.Null(outcome.Fallback);
        Assert.Null(jar.Get(h.Cookies.CookieName));
        Assert.Null(jar.Get(h.Cookies.AltsCookieName));

        await using var db = h.CreateCtx();
        // Every friend session is revoked (that user hit "everywhere")…
        Assert.NotNull((await db.Sessions.FirstAsync(s => s.Id == friendPhone.Id)).RevokedAtUtc);
        // …but the parked OWNER session is forgotten, not revoked.
        Assert.Null((await db.Sessions.FirstAsync(s => s.Id == ownerParked.Id)).RevokedAtUtc);
    }

    [Fact]
    public async Task corrupt_alts_cookie_reads_as_empty_and_is_cleared()
    {
        var h = new Harness();
        var jar = new CookieJar();
        await h.SignInAsync(jar, await h.MintSessionAsync(TestUsers.OwnerId));
        jar.Set(h.Cookies.AltsCookieName, "tampered-garbage");

        var accounts = await h.ListAsync(jar);

        var only = Assert.Single(accounts);
        Assert.Equal(TestUsers.OwnerId, only.UserId);
        Assert.Null(jar.Get(h.Cookies.AltsCookieName));
    }

    // ── harness ───────────────────────────────────────────────────────────────

    /// <summary>Real service stack over EF InMemory + ephemeral data protection.</summary>
    private sealed class Harness
    {
        private readonly DbContextOptions<MusicHoarderDbContext> _dbOptions;

        public AuthService Auth { get; }
        public SessionCookieService Cookies { get; }
        public AccountSwitchService Service { get; }

        public Harness()
        {
            _dbOptions = new DbContextOptionsBuilder<MusicHoarderDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using (var seed = CreateCtx())
            {
                seed.Users.AddRange(
                    NewUser(TestUsers.OwnerId, "owner@example.com", UserRole.Admin, "Owner"),
                    NewUser(TestUsers.DemoId, "demo@example.com", UserRole.Demo, "Demo"),
                    NewUser(TestUsers.FriendId, "friend@example.com", UserRole.Member, "Friend"),
                    NewUser(TestUsers.SecondFriendId, "friend2@example.com", UserRole.Member, "Friend Two"));
                seed.SaveChanges();
            }

            var services = new ServiceCollection();
            services.AddSingleton(_dbOptions);
            services.AddScoped(sp => new MusicHoarderDbContext(sp.GetRequiredService<DbContextOptions<MusicHoarderDbContext>>()));
            var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

            var authOptions = new TestOptionsMonitor<AuthOptions>(new AuthOptions
            {
                OwnerEmail = "owner@example.com",
                DemoUserEmail = "demo@example.com",
                SessionLifetimeDays = 30,
            });

            Auth = new AuthService(
                scopeFactory,
                new ConsoleMagicLinkSender(NullLogger<ConsoleMagicLinkSender>.Instance),
                new TestHostEnvironment("Development"),
                authOptions,
                NullLogger<AuthService>.Instance);
            Cookies = new SessionCookieService(new EphemeralDataProtectionProvider(), authOptions);
            Service = new AccountSwitchService(Auth, Cookies);
        }

        public MusicHoarderDbContext CreateCtx() => new(_dbOptions);

        public async Task<Session> MintSessionAsync(Guid userId)
        {
            var session = await Auth.CreateDeviceSessionAsync(userId, null, null, default);
            Assert.NotNull(session);
            return session!;
        }

        public IReadOnlyList<Guid> SeedExtraFriends(int count)
        {
            var ids = new List<Guid>();
            using var db = CreateCtx();
            for (var i = 0; i < count; i++)
            {
                var id = Guid.NewGuid();
                ids.Add(id);
                db.Users.Add(NewUser(id, $"extra{i}@example.com", UserRole.Member, $"Extra {i}"));
            }
            db.SaveChanges();
            return ids;
        }

        public async Task SignInAsync(CookieJar jar, Session session)
        {
            var ctx = jar.NewRequest();
            await Service.SignInAsync(ctx, session, default);
            jar.Apply(ctx);
        }

        public async Task<IReadOnlyList<AccountView>> ListAsync(CookieJar jar)
        {
            var ctx = jar.NewRequest();
            var accounts = await Service.ListAccountsAsync(ctx, default);
            jar.Apply(ctx);
            return accounts;
        }

        public async Task<AccountView?> SwitchAsync(CookieJar jar, Guid targetUserId)
        {
            var ctx = jar.NewRequest();
            var result = await Service.SwitchAsync(ctx, targetUserId, default);
            jar.Apply(ctx);
            return result;
        }

        public async Task<LogoutOutcome> LogoutAsync(CookieJar jar, bool all)
        {
            var ctx = jar.NewRequest();
            var outcome = await Service.LogoutAsync(ctx, all, default);
            jar.Apply(ctx);
            return outcome;
        }

        private static User NewUser(Guid id, string email, UserRole role, string displayName) => new()
        {
            Id = id,
            Email = email,
            EmailNormalized = User.Normalize(email),
            DisplayName = displayName,
            Role = role,
            CreatedAtUtc = DateTime.UtcNow,
        };
    }

    /// <summary>A minimal browser: applies Set-Cookie responses and replays them as request cookies.</summary>
    private sealed class CookieJar
    {
        private readonly Dictionary<string, string> _cookies = [];

        public string? Get(string name) => _cookies.TryGetValue(name, out var v) ? v : null;

        public void Set(string name, string value) => _cookies[name] = value;

        public DefaultHttpContext NewRequest()
        {
            var ctx = new DefaultHttpContext();
            if (_cookies.Count > 0)
                ctx.Request.Headers.Cookie = string.Join("; ", _cookies.Select(kv => $"{kv.Key}={kv.Value}"));
            return ctx;
        }

        public void Apply(DefaultHttpContext ctx)
        {
            foreach (var header in ctx.Response.Headers.SetCookie)
            {
                if (string.IsNullOrEmpty(header)) continue;
                var firstPart = header.Split(';')[0];
                var eq = firstPart.IndexOf('=');
                var name = firstPart[..eq];
                var value = firstPart[(eq + 1)..];

                var isDelete = string.IsNullOrEmpty(value)
                    || header.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase);
                if (isDelete) _cookies.Remove(name);
                else _cookies[name] = value;
            }
        }
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
