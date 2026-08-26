namespace MusicHoarder.Api.Auth;

/// <summary>
/// A lightweight projection of <see cref="User"/> attached to <c>HttpContext.Items</c> by
/// <see cref="Middleware.AuthenticationMiddleware"/> and read elsewhere via
/// <see cref="ICurrentUserAccessor"/>.
/// </summary>
public sealed record CurrentUser(
    Guid Id,
    string Email,
    UserRole Role,
    string? DisplayName,
    Capability Capabilities = Capability.None)
{
    public bool IsAdmin => Role == UserRole.Admin;
    public bool IsDemo => Role == UserRole.Demo;
    public bool IsMember => Role == UserRole.Member;

    /// <summary>
    /// The capabilities that actually apply. An admin holds every flag regardless of the stored
    /// column, which is what makes the seeded admin row usable and stops an admin locking
    /// themselves out. Always authorize against this, never against <see cref="Capabilities"/>.
    /// </summary>
    public Capability Effective => IsAdmin ? CapabilityDefaults.All : Capabilities;

    public bool Can(Capability capability) => (Effective & capability) == capability;

    [Obsolete("Renamed to IsAdmin. Kept so the rename could be staged; remove once no callers remain.")]
    public bool IsOwner => IsAdmin;

    [Obsolete("Renamed to IsMember. Kept so the rename could be staged; remove once no callers remain.")]
    public bool IsFriend => IsMember;
}
