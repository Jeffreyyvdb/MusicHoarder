namespace MusicHoarder.Api.Auth;

/// <summary>
/// What kind of account this is. The numeric values are the database contract — they are stored
/// in <c>Users.Role</c> and emitted by the <c>HasData</c> seeds — so they must never change.
///
/// <para>
/// Note the deliberate split between <em>role</em> and <em>tenancy</em>: this enum answers "what
/// kind of account is this", while <c>OwnerUserId</c> / <see cref="WellKnownUsers.OwnerId"/>
/// answer "whose rows are these". The word "Owner" belongs to the second question only, which is
/// why renaming this member to <see cref="Admin"/> left every tenancy name untouched.
/// </para>
/// </summary>
public enum UserRole
{
    /// <summary>
    /// Runs the instance. Invites people, grants capabilities, and owns every pipeline and
    /// curation surface. Always has every <see cref="Capability"/> (see
    /// <see cref="CurrentUser.Effective"/>), so an admin can never lock themselves out by
    /// clearing their own flags.
    /// </summary>
    Admin = 0,

    Demo = 1,

    /// <summary>
    /// A real, invited account. What a member may do is decided by the <see cref="Capability"/>
    /// flags an admin grants them, not by this role. Members own no
    /// <see cref="Persistence.SongMetadata"/> rows; they read an admin's music through the
    /// ordinary endpoints, scoped from their <see cref="Persistence.LibraryShareGrant"/> rows.
    /// </summary>
    Member = 2,
}
