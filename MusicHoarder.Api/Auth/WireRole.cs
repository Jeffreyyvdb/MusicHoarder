namespace MusicHoarder.Api.Auth;

/// <summary>
/// Translates <see cref="UserRole"/> to the string clients see, and back.
///
/// <para>
/// The wire vocabulary is deliberately still the pre-rename one — <c>Owner</c>, <c>Demo</c>,
/// <c>Friend</c> — even though the enum now reads <c>Admin</c>, <c>Demo</c>, <c>Member</c>.
/// Shipped Android builds compute <c>isFriend = role == "Friend"</c> and pick their API routes
/// from it, so emitting <c>"Member"</c> would make every old install fall through to the admin
/// routes and collect 403s. APK rollout takes days to weeks and is not under our control, and the
/// client deliberately does not treat 403 as "unpair", so those installs would sit on an empty
/// library with no prompt to re-pair.
/// </para>
///
/// <para>
/// New clients must ignore <c>role</c> entirely and read <c>isAdmin</c> plus <c>capabilities</c>
/// from <c>/api/auth/me</c>. Once no build in the wild reads <c>role</c>, delete this helper and
/// emit <see cref="UserRole"/> directly — that is the one intentionally breaking wire change, and
/// it gets its own release.
/// </para>
/// </summary>
public static class WireRole
{
    public const string Admin = "Owner";
    public const string Demo = "Demo";
    public const string Member = "Friend";

    public static string ToWire(UserRole role) => role switch
    {
        UserRole.Admin => Admin,
        UserRole.Demo => Demo,
        UserRole.Member => Member,
        _ => role.ToString(),
    };

    /// <summary>Every capability the account effectively holds, as stable string names.</summary>
    public static string[] ToWire(Capability capabilities) =>
        Enum.GetValues<Capability>()
            .Where(c => c != Capability.None && (capabilities & c) == c)
            .Select(c => c.ToString())
            .ToArray();
}
