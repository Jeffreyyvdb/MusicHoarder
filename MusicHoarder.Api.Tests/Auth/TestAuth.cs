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
        new(TestUsers.OwnerId, "owner@test.local", UserRole.Owner, "Owner");

    public static CurrentUser DemoUser =>
        new(TestUsers.DemoId, "demo@test.local", UserRole.Demo, "Demo");
}
