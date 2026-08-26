using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Endpoints;

/// <summary>
/// Admin-side capability management. The interesting cases are the two ways an instance could be
/// bricked — an admin clearing their own admin bit, and the last admin being demoted — plus the
/// rule that a typo'd capability name is rejected rather than read as a revoke.
/// </summary>
public class CapabilityManagementTests
{
    private static readonly Guid SecondAdminId = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ThirdAdminId = new("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task Granting_a_capability_stores_it_and_echoes_the_effective_set()
    {
        var options = NewOptions();
        await SeedUsers(options);

        var payload = await Update(options, TestUsers.FriendId, ["TrackListening", "DownloadMusic"]);

        Assert.Equal(
            new[] { "DownloadMusic", "TrackListening" },
            Get<string[]>(payload, "Capabilities").Order().ToArray());

        await using var verify = new MusicHoarderDbContext(options);
        var member = await verify.Users.SingleAsync(u => u.Id == TestUsers.FriendId);
        Assert.Equal(Capability.DownloadMusic | Capability.TrackListening, member.Capabilities);
        Assert.Equal(UserRole.Member, member.Role);
    }

    [Fact]
    public async Task Sending_the_whole_set_revokes_what_is_absent()
    {
        // The request is the desired state, not a delta — that is what makes it idempotent.
        var options = NewOptions();
        await SeedUsers(options, memberCapabilities: Capability.TrackListening | Capability.DownloadMusic);

        await Update(options, TestUsers.FriendId, ["TrackListening"]);

        await using var verify = new MusicHoarderDbContext(options);
        Assert.Equal(
            Capability.TrackListening,
            (await verify.Users.SingleAsync(u => u.Id == TestUsers.FriendId)).Capabilities);
    }

    [Fact]
    public async Task Granting_Administer_promotes_the_account_to_admin()
    {
        var options = NewOptions();
        await SeedUsers(options);

        var payload = await Update(options, TestUsers.FriendId, ["Administer"]);
        Assert.True(Get<bool>(payload, "IsAdmin"));

        await using var verify = new MusicHoarderDbContext(options);
        Assert.Equal(UserRole.Admin, (await verify.Users.SingleAsync(u => u.Id == TestUsers.FriendId)).Role);
    }

    [Fact]
    public async Task Withdrawing_Administer_demotes_back_to_member()
    {
        var options = NewOptions();
        await SeedUsers(options, secondAdmin: true);

        await Update(options, SecondAdminId, ["TrackListening"]);

        await using var verify = new MusicHoarderDbContext(options);
        var demoted = await verify.Users.SingleAsync(u => u.Id == SecondAdminId);
        Assert.Equal(UserRole.Member, demoted.Role);
        Assert.Equal(Capability.TrackListening, demoted.Capabilities);
    }

    [Fact]
    public async Task An_admin_cannot_change_their_own_capabilities()
    {
        // Clearing your own Administer bit would 403 you out of this endpoint immediately, leaving
        // no way back except the database.
        var options = NewOptions();
        await SeedUsers(options);

        var result = await Invoke(options, TestUsers.OwnerId, []);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.Equal("cannot_change_own_capabilities", ErrorOf(result));
    }

    [Fact]
    public async Task The_last_admin_cannot_be_demoted()
    {
        // Seed a second admin, demote them, then try to demote... there is no one left to demote
        // except the caller, so verify via a third account promoted and then demoted while the
        // caller is the only other admin — the guard only fires when NO other admin would remain.
        var options = NewOptions();
        await SeedUsers(options, secondAdmin: true);

        // Remove the caller's admin role directly, leaving SecondAdmin as the only admin, and have
        // SecondAdmin be the one the (now sole) caller tries to demote.
        await using (var edit = new MusicHoarderDbContext(options))
        {
            var caller = await edit.Users.SingleAsync(u => u.Id == TestUsers.OwnerId);
            caller.Role = UserRole.Member;
            await edit.SaveChangesAsync();
        }

        var result = await Invoke(options, SecondAdminId, ["TrackListening"]);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.Equal("last_admin", ErrorOf(result));

        await using var verify = new MusicHoarderDbContext(options);
        Assert.Equal(UserRole.Admin, (await verify.Users.SingleAsync(u => u.Id == SecondAdminId)).Role);
    }

    [Fact]
    public async Task A_disabled_account_cannot_be_promoted_to_admin()
    {
        // A disabled account cannot sign in, so promoting one manufactures an admin that can never
        // act — and it would then count as cover for demoting the only admin who can.
        var options = NewOptions();
        await SeedUsers(options, disabledMember: true);

        var result = await Invoke(options, TestUsers.FriendId, ["Administer"]);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.Equal("cannot_promote_disabled_account", ErrorOf(result));
    }

    [Fact]
    public async Task A_disabled_admin_does_not_count_as_the_other_admin()
    {
        // Seed a disabled admin plus the caller. Demoting the caller must still be refused: the
        // disabled row cannot sign in, so allowing it would leave nobody able to administer.
        var options = NewOptions();
        await SeedUsers(options, secondAdmin: true, disableSecondAdmin: true);

        await using (var edit = new MusicHoarderDbContext(options))
        {
            // Make the caller demotable by someone else: a third admin issues the request.
            edit.Users.Add(User(ThirdAdminId, "third@test.local", UserRole.Admin));
            await edit.SaveChangesAsync();
        }

        // Third admin demotes the caller; only the DISABLED second admin would remain besides
        // the third — so this must succeed. Then the third demotes itself-equivalent target.
        var demoteCaller = await Invoke(options, TestUsers.OwnerId, [], caller: ThirdAdminId);
        Assert.Equal(StatusCodes.Status200OK, StatusOf(demoteCaller));

        // Now only the third admin is active (second is disabled). Demoting it must be refused.
        var result = await Invoke(options, ThirdAdminId, [], caller: TestUsers.OwnerId);
        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.Equal("last_admin", ErrorOf(result));
    }

    [Theory]
    [InlineData("8")]
    [InlineData("2")]
    [InlineData("Administer,DownloadMusic")]
    public async Task Capabilities_must_be_named_never_numeric(string raw)
    {
        // Enum.TryParse reads "8" as Administer, so a numeric value would grant admin without ever
        // naming it — and a typo'd number lands on whatever flag shares that value.
        var options = NewOptions();
        await SeedUsers(options);

        var result = await Invoke(options, TestUsers.FriendId, [raw]);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.Equal("unknown_capability", ErrorOf(result));
    }

    [Fact]
    public async Task An_unknown_capability_name_is_rejected_not_ignored()
    {
        // Ignoring it would read as "revoke that one", which is the opposite of what was meant.
        var options = NewOptions();
        await SeedUsers(options, memberCapabilities: Capability.TrackListening);

        var result = await Invoke(options, TestUsers.FriendId, ["TrackListening", "DownloadMusicc"]);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.Equal("unknown_capability", ErrorOf(result));

        await using var verify = new MusicHoarderDbContext(options);
        Assert.Equal(
            Capability.TrackListening,
            (await verify.Users.SingleAsync(u => u.Id == TestUsers.FriendId)).Capabilities);
    }

    [Fact]
    public async Task None_is_not_an_acceptable_capability_name()
    {
        var options = NewOptions();
        await SeedUsers(options);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(await Invoke(options, TestUsers.FriendId, ["None"])));
    }

    [Fact]
    public async Task An_empty_set_revokes_everything()
    {
        var options = NewOptions();
        await SeedUsers(options, memberCapabilities: CapabilityDefaults.All);

        await Update(options, TestUsers.FriendId, []);

        await using var verify = new MusicHoarderDbContext(options);
        var member = await verify.Users.SingleAsync(u => u.Id == TestUsers.FriendId);
        Assert.Equal(Capability.None, member.Capabilities);
        Assert.Equal(UserRole.Member, member.Role);
    }

    [Fact]
    public async Task The_demo_account_cannot_be_granted_anything()
    {
        // Demo credentials are shared by every visitor; capabilities there would be public.
        var options = NewOptions();
        await SeedUsers(options);

        var result = await Invoke(options, TestUsers.DemoId, ["TrackListening"]);

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
    }

    [Fact]
    public async Task Listing_people_includes_other_admins_so_they_can_be_demoted()
    {
        var options = NewOptions();
        await SeedUsers(options, secondAdmin: true);

        await using var db = new MusicHoarderDbContext(options, new TestCurrentUserAccessor(Caller));
        var result = await FriendsEndpoints.ListFriends(db, new TestCurrentUserAccessor(Caller), default);
        var people = ((System.Collections.IEnumerable)result.GetType().GetProperty("Value")!.GetValue(result)!)
            .Cast<object>().ToList();

        var ids = people.Select(p => Get<Guid>(p, "Id")).ToList();
        Assert.Contains(SecondAdminId, ids);
        Assert.Contains(TestUsers.FriendId, ids);
        // Never the caller (nothing to manage) and never the shared demo login.
        Assert.DoesNotContain(TestUsers.OwnerId, ids);
        Assert.DoesNotContain(TestUsers.DemoId, ids);
    }

    // --- helpers -----------------------------------------------------------------------------

    private static CurrentUser Caller =>
        new(TestUsers.OwnerId, "admin@test.local", UserRole.Admin, "Admin");

    /// <summary>
    /// The in-memory provider cannot do transactions and throws rather than ignoring one, so the
    /// demotion path (which opens a serializable transaction to close a TOCTOU window) needs this
    /// warning suppressed to run at all.
    ///
    /// <para>
    /// Be clear about what that costs: with the transaction no-op'd, these tests verify the guard's
    /// LOGIC but not its ISOLATION. The "two admins demote each other concurrently" race is
    /// therefore not covered here — the in-memory store has no isolation levels to model it with.
    /// That behaviour rests on Postgres and on review.
    /// </para>
    /// </summary>
    private static DbContextOptions<MusicHoarderDbContext> NewOptions() =>
        new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static async Task<IResult> Invoke(
        DbContextOptions<MusicHoarderDbContext> options,
        Guid targetId,
        string[] capabilities,
        Guid? caller = null)
    {
        var acting = caller is { } id
            ? new CurrentUser(id, "acting@test.local", UserRole.Admin, "Acting")
            : Caller;
        await using var db = new MusicHoarderDbContext(options, new TestCurrentUserAccessor(acting));
        return await FriendsEndpoints.UpdateCapabilities(
            targetId,
            new FriendsEndpoints.UpdateCapabilitiesRequest(capabilities),
            db,
            new TestCurrentUserAccessor(acting),
            default);
    }

    private static async Task<object> Update(
        DbContextOptions<MusicHoarderDbContext> options, Guid targetId, string[] capabilities)
    {
        var result = await Invoke(options, targetId, capabilities);
        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
        return result.GetType().GetProperty("Value")!.GetValue(result)!;
    }

    private static int StatusOf(IResult result) =>
        result.GetType().GetProperty("StatusCode")?.GetValue(result) as int? ?? StatusCodes.Status200OK;

    private static string? ErrorOf(IResult result)
    {
        var value = result.GetType().GetProperty("Value")?.GetValue(result);
        return value?.GetType().GetProperty("error")?.GetValue(value) as string;
    }

    private static T Get<T>(object target, string name) =>
        (T)target.GetType().GetProperty(name)!.GetValue(target)!;

    private static async Task SeedUsers(
        DbContextOptions<MusicHoarderDbContext> options,
        Capability memberCapabilities = Capability.None,
        bool secondAdmin = false,
        bool disableSecondAdmin = false,
        bool disabledMember = false)
    {
        await using var db = new MusicHoarderDbContext(options);
        var member = User(TestUsers.FriendId, "member@test.local", UserRole.Member, memberCapabilities);
        member.IsDisabled = disabledMember;
        db.Users.AddRange(
            User(TestUsers.OwnerId, "admin@test.local", UserRole.Admin),
            User(TestUsers.DemoId, "demo@test.local", UserRole.Demo),
            member);
        if (secondAdmin)
        {
            var second = User(SecondAdminId, "second@test.local", UserRole.Admin);
            second.IsDisabled = disableSecondAdmin;
            db.Users.Add(second);
        }
        await db.SaveChangesAsync();
    }

    private static User User(
        Guid id, string email, UserRole role, Capability capabilities = Capability.None) => new()
    {
        Id = id,
        Email = email,
        EmailNormalized = Api.Auth.User.Normalize(email),
        DisplayName = email.Split('@')[0],
        Role = role,
        Capabilities = capabilities,
        CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };
}
