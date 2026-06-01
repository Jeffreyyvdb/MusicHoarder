using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// A per-user, per-folder preference for the "Match by folder" view. Folders are identified by
/// their <em>source-relative</em> path (exactly the <c>Path</c> the directory-tree emits, e.g.
/// <c>"Kanye West/Donda"</c>; <c>""</c> for the root). Currently carries a single flag,
/// <see cref="ExpectedLow"/>, which lets a user mark leaks / unreleased / field recordings that
/// will never match a public database so they stop reading as problems in the work queue.
/// </summary>
public class DirectoryPreference
{
    [Key]
    public int Id { get; set; }

    /// <summary>Owner of the folder view. Scopes the preference per-user via the tenancy filter.</summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>Source-relative folder path, matching the directory-tree node's <c>Path</c>.</summary>
    [MaxLength(2048)]
    public string Path { get; set; } = string.Empty;

    /// <summary>When set, this folder is expected to have a low match rate and is pulled out of the work queue.</summary>
    public bool ExpectedLow { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
