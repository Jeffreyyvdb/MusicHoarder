namespace MusicHoarder.Api.Auth;

/// <summary>
/// What an account is allowed to do, independent of its <see cref="UserRole"/>. Stored as an int
/// on <c>Users.Capabilities</c> and projected onto <see cref="CurrentUser"/> by
/// <see cref="Middleware.AuthenticationMiddleware"/>.
///
/// <para>
/// Read the effective set through <see cref="CurrentUser.Can"/>, never the raw column: an
/// <see cref="UserRole.Admin"/> always has every flag regardless of what is stored, which is what
/// keeps the seeded admin row (<c>Capabilities = 0</c> from the column default) working and makes
/// it impossible for an admin to lock themselves out through the toggle UI.
/// </para>
///
/// <para>
/// Values are persisted, so they must never be renumbered. Add new flags at the next free bit.
/// </para>
/// </summary>
[Flags]
public enum Capability
{
    None = 0,

    /// <summary>
    /// May request downloads and manage wishlist entries.
    ///
    /// <para>
    /// DEFINED BUT NOT WIRED. The download pipeline is single-tenant — every background service
    /// stamps <c>IOwnerLookupService.OwnerUserId</c> — so granting this flag currently changes
    /// nothing beyond what the admin UI shows. Wiring it means making the pipeline genuinely
    /// multi-tenant, which is a separate project. Do not gate <c>/api/wishlist</c> on it until
    /// then, or a member will get a surface that silently writes into the admin's library.
    /// </para>
    /// </summary>
    DownloadMusic = 1 << 0,

    /// <summary>
    /// May like tracks and record plays. For a member these writes land in
    /// <see cref="Persistence.UserSongState"/>, never on the granting admin's song row.
    /// </summary>
    TrackListening = 1 << 1,

    /// <summary>May re-share music that was granted to them.</summary>
    ManageOwnShares = 1 << 2,

    /// <summary>
    /// Full instance rights. Held implicitly by every <see cref="UserRole.Admin"/>; granting it to
    /// a member is done by promoting the role, not by setting the bit alone.
    /// </summary>
    Administer = 1 << 3,
}

public static class CapabilityDefaults
{
    /// <summary>Every defined flag. What an <see cref="UserRole.Admin"/> effectively holds.</summary>
    public const Capability All =
        Capability.DownloadMusic
        | Capability.TrackListening
        | Capability.ManageOwnShares
        | Capability.Administer;

    /// <summary>
    /// What a freshly invited member starts with. Listening only — everything else is an explicit
    /// admin decision. Matches the backfill applied to pre-existing rows by the
    /// <c>AddUserCapabilities</c> migration, so invited-before and invited-after members behave
    /// the same.
    /// </summary>
    public const Capability NewMember = Capability.TrackListening;
}
