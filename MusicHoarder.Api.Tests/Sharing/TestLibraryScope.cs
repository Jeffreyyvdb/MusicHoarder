using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Sharing;

namespace MusicHoarder.Api.Tests.Sharing;

/// <summary>
/// Builds the real <see cref="LibraryScopeResolver"/> for tests.
///
/// <para>
/// Deliberately the production type, not a stub. These endpoints authorize through the resolver,
/// so a fake here would quietly turn every endpoint test into a test of the fake. Tests that pass
/// no caller get an anonymous resolver, which resolves nothing — matching a context built without
/// an <see cref="ICurrentUserAccessor"/>, where the ambient filter is also off.
/// </para>
/// </summary>
internal static class TestLibraryScope
{
    internal static ILibraryScopeResolver For(CurrentUser? caller = null) =>
        new LibraryScopeResolver(
            new Api.Tests.Auth.TestCurrentUserAccessor(caller),
            new SharedLibraryGrantResolver());

    internal static ILibraryScopeResolver For(Guid callerId) =>
        For(new CurrentUser(callerId, "caller@test.local", UserRole.Member, null));
}
