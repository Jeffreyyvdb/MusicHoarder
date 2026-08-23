namespace MusicHoarder.Api.Persistence;

/// <summary>
/// A friend's personal listening state on one of the owner's songs — the per-user counterpart of
/// the like/play columns that live directly on <see cref="SongMetadata"/> for the owner. One row
/// per (friend, song), created lazily on the first like/play through the grant-scoped
/// <c>/api/shared</c> endpoints (which verify the song is in the caller's grant scope before
/// writing). Rows survive grant revocation but are inert then: the song no longer lists, so the
/// state simply resurfaces if the grant comes back. Deliberately NOT wired into the Navidrome /
/// instance-sync like propagation — those mirror the owner's taste, not guests'.
/// </summary>
public class FriendSongState
{
    public int Id { get; set; }

    /// <summary>The friend (grantee). Not the song's owner.</summary>
    public Guid UserId { get; set; }

    public int SongId { get; set; }

    public SongMetadata? Song { get; set; }

    public DateTime? LikedAtUtc { get; set; }

    public int PlayCount { get; set; }

    public DateTime? LastPlayedAtUtc { get; set; }
}
