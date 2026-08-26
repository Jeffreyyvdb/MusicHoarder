using MusicHoarder.Api.Auth;

namespace MusicHoarder.Api.Tests.Auth;

/// <summary>
/// Shared test helpers for code that depends on identity-aware services. Mirrors the GUIDs in
/// <see cref="WellKnownUsers"/> so tests interoperate with the EF <c>HasData</c> seeds.
/// </summary>
internal static class TestUsers
{
    public static Guid OwnerId => WellKnownUsers.OwnerId;
    public static Guid DemoId => WellKnownUsers.DemoId;

    /// <summary>A friend account. Unlike Owner/Demo there is no well-known GUID — friends are
    /// runtime-created rows — so tests pin an arbitrary fixed one.</summary>
    public static Guid FriendId { get; } = new("f1e2d3c4-b5a6-4798-8a9b-0c1d2e3f4a5b");

    /// <summary>A second friend, for isolation tests (friend B must not see friend A's grants).</summary>
    public static Guid SecondFriendId { get; } = new("0a1b2c3d-4e5f-4a6b-8c9d-e0f1a2b3c4d5");
}

internal sealed class TestOwnerLookupService : IOwnerLookupService
{
    public Guid OwnerUserId { get; init; } = WellKnownUsers.OwnerId;
}

internal sealed class TestCurrentUserAccessor : ICurrentUserAccessor
{
    public TestCurrentUserAccessor(CurrentUser? user = null)
    {
        User = user;
    }

    public CurrentUser? User { get; }
    public Guid UserId => User?.Id ?? Guid.Empty;

    public static CurrentUser OwnerUser =>
        new(TestUsers.OwnerId, "owner@test.local", UserRole.Admin, "Owner");

    public static CurrentUser DemoUser =>
        new(TestUsers.DemoId, "demo@test.local", UserRole.Demo, "Demo");

    public static CurrentUser FriendUser =>
        new(TestUsers.FriendId, "friend@test.local", UserRole.Member, "Friend");
}
