using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Library;

/// <summary>
/// The album-level identity tags that every track of one album must share so a music server
/// (Navidrome groups first on the MusicBrainz <i>release</i> id) doesn't split a single on-disk
/// album into several. Carries <b>only</b> album-level fields — track-level fields (title, track/disc
/// number, performer, recording id, ISRC) are deliberately absent, which is the compile-time
/// guarantee that the reconciler can never touch them. <see cref="TotalTracks"/> is intentionally not
/// here: it is per-disc on multi-disc releases and is not part of any album-grouping key, so the tag
/// writer keeps reading it from the individual song row.
/// </summary>
public sealed record AlbumIdentity(
    string? Album,
    string? AlbumArtist,
    int? Year,
    bool IsCompilation,
    int? TotalDiscs,
    string? ReleaseTypePrimary,
    string? ReleaseTypes,
    string? MusicBrainzReleaseId,
    string? MusicBrainzReleaseGroupId,
    string? AlbumArtistMusicBrainzId)
{
    /// <summary>
    /// The unharmonized identity straight off a single song — used when reconciliation is disabled
    /// or for a folder with no electable membership (e.g. unreleased singles). Reproduces the
    /// pre-reconciliation tag-writing behavior exactly.
    /// </summary>
    public static AlbumIdentity FromSong(SongMetadata song) => new(
        song.Album,
        song.AlbumArtist,
        song.Year,
        song.IsCompilation,
        song.TotalDiscs,
        song.ReleaseTypePrimary,
        song.ReleaseTypes,
        song.MusicBrainzReleaseId,
        song.MusicBrainzReleaseGroupId,
        song.AlbumArtistMusicBrainzId);
}

public interface IAlbumIdentityReconciler
{
    /// <summary>
    /// Elects one canonical <see cref="AlbumIdentity"/> for the set of songs that all build into the
    /// same destination album folder, so they are tagged consistently. Deterministic: the same
    /// membership always elects the same identity, so rebuilds don't thrash files.
    /// </summary>
    AlbumIdentity Reconcile(IReadOnlyList<SongMetadata> albumMembers);
}
