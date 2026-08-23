namespace MusicHoarder.Api.Auth;

public enum UserRole
{
    Owner = 0,
    Demo = 1,

    /// <summary>
    /// An invited listener: signs in like the owner (magic link) but is read-only
    /// (<see cref="Middleware.FriendReadOnlyMiddleware"/>) and only sees music the owner
    /// explicitly shared via <see cref="Persistence.LibraryShareGrant"/> rows.
    /// </summary>
    Friend = 2,
}
