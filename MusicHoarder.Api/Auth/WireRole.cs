namespace MusicHoarder.Api.Auth;

/// <summary>
/// How an account's role and capabilities appear on the wire (<c>/api/auth/me</c>, the account
/// switcher, Friends). The role is the <see cref="UserRole"/> name: <c>Admin</c>, <c>Demo</c>,
/// <c>Member</c>.
///
/// <para>
/// Until the native Android client was retired it was the pre-rename vocabulary (<c>Owner</c>,
/// <c>Demo</c>, <c>Friend</c>), because that client picked its API routes from it. Clients
/// should still branch on <c>isAdmin</c> and <c>capabilities</c>, never on <c>role</c>, which is
/// for display.
/// </para>
/// </summary>
public static class WireRole
{
    public static string ToWire(UserRole role) => role.ToString();

    /// <summary>Every capability the account effectively holds, as stable string names.</summary>
    public static string[] ToWire(Capability capabilities) =>
        Enum.GetValues<Capability>()
            .Where(c => c != Capability.None && (capabilities & c) == c)
            .Select(c => c.ToString())
            .ToArray();
}
