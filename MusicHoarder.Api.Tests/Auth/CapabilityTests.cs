using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;

namespace MusicHoarder.Api.Tests.Auth;

/// <summary>
/// Pins the capability model: what an account effectively holds, what the endpoint filter does
/// with it, and the legacy vocabulary the wire still speaks.
/// </summary>
public class CapabilityTests
{
    private static CurrentUser Member(Capability capabilities) =>
        new(TestUsers.FriendId, "member@test.local", UserRole.Member, "Member", capabilities);

    // --- Effective capabilities -------------------------------------------------------------

    [Fact]
    public void Admin_holds_every_capability_even_with_an_empty_column()
    {
        // The seeded admin row stores Capabilities = 0. If authorization read the column instead
        // of Effective, a fresh instance would lock its own admin out of every endpoint.
        var admin = new CurrentUser(TestUsers.OwnerId, "admin@test.local", UserRole.Admin, "Admin");

        Assert.Equal(CapabilityDefaults.All, admin.Effective);
        Assert.True(admin.Can(Capability.Administer));
        Assert.True(admin.Can(Capability.DownloadMusic));
        Assert.True(admin.Can(Capability.TrackListening));
        Assert.True(admin.Can(Capability.ManageOwnShares));
    }

    [Fact]
    public void Member_holds_only_what_was_granted()
    {
        var member = Member(Capability.TrackListening);

        Assert.True(member.Can(Capability.TrackListening));
        Assert.False(member.Can(Capability.Administer));
        Assert.False(member.Can(Capability.DownloadMusic));
        Assert.False(member.Can(Capability.ManageOwnShares));
    }

    [Fact]
    public void Demo_holds_nothing_by_default()
    {
        var demo = new CurrentUser(TestUsers.DemoId, "demo@test.local", UserRole.Demo, "Demo");

        Assert.Equal(Capability.None, demo.Effective);
        Assert.False(demo.Can(Capability.Administer));
        Assert.False(demo.Can(Capability.TrackListening));
    }

    [Fact]
    public void Can_requires_every_bit_of_a_combined_capability()
    {
        var member = Member(Capability.TrackListening);

        Assert.False(member.Can(Capability.TrackListening | Capability.DownloadMusic));
        Assert.True(Member(Capability.TrackListening | Capability.DownloadMusic)
            .Can(Capability.TrackListening | Capability.DownloadMusic));
    }

    [Fact]
    public void New_members_start_with_listening_only()
    {
        // Must match the AddUserCapabilities backfill (Capabilities = 2 for Role = 2), so a member
        // invited before capabilities existed and one invited after behave identically.
        Assert.Equal(Capability.TrackListening, CapabilityDefaults.NewMember);
        Assert.Equal(2, (int)CapabilityDefaults.NewMember);
    }

    [Fact]
    public void Capability_values_are_the_persisted_contract()
    {
        // These are stored as an int column. Renumbering silently re-points every granted flag.
        Assert.Equal(0, (int)Capability.None);
        Assert.Equal(1, (int)Capability.DownloadMusic);
        Assert.Equal(2, (int)Capability.TrackListening);
        Assert.Equal(4, (int)Capability.ManageOwnShares);
        Assert.Equal(8, (int)Capability.Administer);
    }

    [Fact]
    public void Role_values_are_the_persisted_contract()
    {
        // The Admin/Member rename was source-only precisely because these did not move.
        Assert.Equal(0, (int)UserRole.Admin);
        Assert.Equal(1, (int)UserRole.Demo);
        Assert.Equal(2, (int)UserRole.Member);
    }

    // --- RequireCapability filter -----------------------------------------------------------

    [Fact]
    public async Task RequireCapability_rejects_anonymous_with_401()
    {
        var result = await Invoke(null, Capability.TrackListening);
        Assert.Equal(StatusCodes.Status401Unauthorized, StatusCodeOf(result));
    }

    [Fact]
    public async Task RequireCapability_rejects_a_member_without_the_flag()
    {
        var result = await Invoke(Member(Capability.TrackListening), Capability.DownloadMusic);
        Assert.Equal(StatusCodes.Status403Forbidden, StatusCodeOf(result));
    }

    [Fact]
    public async Task RequireCapability_admits_a_member_holding_the_flag()
    {
        var result = await Invoke(Member(Capability.TrackListening), Capability.TrackListening);
        Assert.Equal("next", result);
    }

    [Fact]
    public async Task RequireAdmin_admits_an_admin_and_rejects_a_member()
    {
        var admin = new CurrentUser(TestUsers.OwnerId, "admin@test.local", UserRole.Admin, "Admin");

        Assert.Equal("next", await Invoke(admin, Capability.Administer));
        Assert.Equal(
            StatusCodes.Status403Forbidden,
            StatusCodeOf(await Invoke(Member(CapabilityDefaults.NewMember), Capability.Administer)));
    }

    [Fact]
    public async Task RequireAdmin_rejection_carries_the_admin_required_code()
    {
        // The frontend maps this string to friendly copy; changing it silently degrades every
        // admin-only endpoint's error into raw JSON.
        var result = await Invoke(Member(Capability.None), Capability.Administer, "admin_required");
        Assert.Equal("admin_required", ErrorOf(result));
    }

    // --- Wire vocabulary ---------------------------------------------------------------------

    [Theory]
    [InlineData(UserRole.Admin, "Owner")]
    [InlineData(UserRole.Demo, "Demo")]
    [InlineData(UserRole.Member, "Friend")]
    public void Wire_role_still_speaks_the_pre_rename_vocabulary(UserRole role, string expected)
    {
        // Shipped Android builds branch on role == "Friend" to pick their API routes. Emitting
        // "Member" would send every old install down the admin routes and into 403s, with no
        // re-pair prompt. Flip this only in the release that intentionally breaks old clients.
        Assert.Equal(expected, WireRole.ToWire(role));
    }

    [Fact]
    public void Wire_capabilities_are_stable_names_and_omit_None()
    {
        Assert.Equal(
            ["DownloadMusic", "TrackListening"],
            WireRole.ToWire(Capability.DownloadMusic | Capability.TrackListening));
        Assert.Empty(WireRole.ToWire(Capability.None));
    }

    [Fact]
    public void Wire_capabilities_for_an_admin_list_everything()
    {
        var admin = new CurrentUser(TestUsers.OwnerId, "admin@test.local", UserRole.Admin, "Admin");

        Assert.Equal(
            ["DownloadMusic", "TrackListening", "ManageOwnShares", "Administer"],
            WireRole.ToWire(admin.Effective));
    }

    // --- Helpers -----------------------------------------------------------------------------

    private static async Task<object?> Invoke(
        CurrentUser? currentUser, Capability required, string errorCode = "capability_required")
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUserAccessor>(new TestCurrentUserAccessor(currentUser));
        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        var context = new DefaultEndpointFilterInvocationContext(httpContext);

        var filter = new RequireCapabilityFilter(required, errorCode);
        return await filter.InvokeAsync(context, _ => ValueTask.FromResult<object?>("next"));
    }

    private static int? StatusCodeOf(object? result) =>
        result?.GetType().GetProperty("StatusCode")?.GetValue(result) as int?;

    private static string? ErrorOf(object? result)
    {
        var value = result?.GetType().GetProperty("Value")?.GetValue(result);
        return value?.GetType().GetProperty("error")?.GetValue(value) as string;
    }
}
