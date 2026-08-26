namespace MusicHoarder.Api.Persistence;

/// <summary>
/// One account's personal listening state on a song it does not own — the per-user counterpart of
/// the like/play columns that live directly on <see cref="SongMetadata"/> for the account that
/// owns the row.
///
/// <para>
/// The rule this encodes, in one sentence: <b>you write like/play to the song row's own columns if
/// you own it, and to a <see cref="UserSongState"/> row if you do not.</b> One row per
/// (user, song), created lazily on the first like or play, and only after the caller's grants have
/// been verified to expose that song. Rows survive grant revocation but are inert then — the song
/// stops listing, so the state simply resurfaces if the grant comes back.
/// </para>
///
/// <para>
/// Deliberately NOT wired into the Navidrome or instance-sync like propagation. Those mirror the
/// library owner's taste, not a guest's, so the enqueue calls must stay on the owns-the-row branch.
/// </para>
/// </summary>
public class UserSongState
{
    public int Id { get; set; }

    /// <summary>The listener (grantee). Not the song's owner.</summary>
    public Guid UserId { get; set; }

    public int SongId { get; set; }

    public SongMetadata? Song { get; set; }

    public DateTime? LikedAtUtc { get; set; }

    public int PlayCount { get; set; }

    public DateTime? LastPlayedAtUtc { get; set; }
}
