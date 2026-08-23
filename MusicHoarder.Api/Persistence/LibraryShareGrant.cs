using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

public enum ShareGrantScope
{
    Album = 0,
    Artist = 1,

    /// <summary>The owner's whole built library (rows with <c>LibraryBuildStatus == Done</c>).</summary>
    Library = 2,
}

/// <summary>
/// An account-to-account share: the owner grants one friend read access to an album, an artist,
/// or the whole library. Unlike <see cref="SongShare"/> there is no capability token — the
/// grantee's session is the capability, and the read-only <c>/api/shared</c> endpoints resolve
/// the caller's grants and re-scope every song read to the grant's own <see cref="OwnerUserId"/>
/// plus membership, mirroring the anonymous-share pattern. Membership keys use the exact same
/// derivation as <c>SharesEndpoints.LoadSongsInScopeAsync</c>: lowercased
/// <c>(AlbumArtist ?? Artist, Album)</c>, deliberately not year (per-track enrichment can leave
/// one album's tracks with inconsistent years — see CLAUDE.md). Revoking sets
/// <see cref="RevokedAtUtc"/>; the friend's view updates on their next fetch.
/// </summary>
public class LibraryShareGrant
{
    public int Id { get; set; }

    /// <summary>The grantor — whose songs the grant exposes.</summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>The friend the grant is for.</summary>
    public Guid GranteeUserId { get; set; }

    public ShareGrantScope Scope { get; set; }

    /// <summary>Lowercased <c>(AlbumArtist ?? Artist) ?? ""</c>. Null for Library scope.</summary>
    [MaxLength(512)]
    public string? ArtistKey { get; set; }

    /// <summary>Lowercased album title. Null for Artist/Library scope.</summary>
    [MaxLength(512)]
    public string? AlbumKey { get; set; }

    /// <summary>Original casing, for the management UI only — matching always uses the keys.</summary>
    [MaxLength(512)]
    public string? ArtistDisplay { get; set; }

    [MaxLength(512)]
    public string? AlbumDisplay { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? RevokedAtUtc { get; set; }

    public bool IsActive => RevokedAtUtc is null;
}
