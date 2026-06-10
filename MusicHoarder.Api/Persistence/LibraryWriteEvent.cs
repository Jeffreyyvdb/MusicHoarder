using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// One change MusicHoarder actually wrote to the <b>destination</b> library — the files Navidrome
/// reads. Emitted only at build / re-tag time (when tags physically land on disk), not at enrichment
/// time, so the History feed reflects exactly "what Navidrome will see differently". A
/// <see cref="LibraryWriteEventKind.TrackTagsWritten"/> row is one changed field on one track
/// (old → new); a <see cref="LibraryWriteEventKind.AlbumCoverWritten"/> row is an album-folder cover
/// write and carries no field diff. Append-only audit data — never soft-deleted.
/// </summary>
public class LibraryWriteEvent
{
    [Key]
    public long Id { get; set; }

    /// <summary>
    /// Owner of the song this write belongs to. Set explicitly from the song row by the background
    /// builder (no current user in that scope). Scoped by the EF global query filter.
    /// </summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>
    /// The build batch run that produced this write. Soft grouping key only — NOT a foreign key to
    /// <see cref="IngestRun"/> (the builder's batch run id and the ingest-run id are distinct concepts).
    /// </summary>
    public Guid? RunId { get; set; }

    /// <summary>Null for album-level events (e.g. a cover write) that don't belong to a single track.</summary>
    public int? SongId { get; set; }
    public SongMetadata? Song { get; set; }

    public LibraryWriteEventKind Kind { get; set; }

    public DateTime WrittenAtUtc { get; set; }

    // Destination grouping keys captured at write time so feed rollups/filters need no join and
    // survive even if the song row later changes album/artist.
    public string? DestinationPath { get; set; }
    public string? AlbumFolder { get; set; }

    [MaxLength(512)]
    public string? AlbumArtist { get; set; }

    [MaxLength(512)]
    public string? Album { get; set; }

    // Field-level diff. For TrackTagsWritten: one row per changed field. For AlbumCoverWritten:
    // FieldName = "Cover", NewValue = "written", OldValue null.
    [MaxLength(64)]
    public string? FieldName { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }

    /// <summary>
    /// True when the field is an album-IDENTITY field (Album/AlbumArtist/Year/release ids/disc count/
    /// compilation/release types) rather than a track-level field — lets the feed label consolidation
    /// distinctly from a plain tag rewrite.
    /// </summary>
    public bool IsAlbumIdentityField { get; set; }
}

public enum LibraryWriteEventKind
{
    TrackTagsWritten = 0,
    AlbumCoverWritten = 1,
}
