namespace MusicHoarder.Api.Contracts;

/// <summary>
/// One album card. The aggregated view of every song that groups together under
/// <see cref="Key"/> — see <c>AlbumProjection</c> for the grouping rules themselves.
///
/// <para>
/// Deliberately carries track <b>ids</b> rather than track rows. Both clients already hold the whole
/// library from <c>GET /songs</c>, and the rows they hold are the ones their optimistic like/play
/// overlays mutate in place; an album that shipped its own copies would show a stale heart and a
/// stale play count until the next refetch. Joining ids against the list they already have keeps
/// that live, and keeps this payload a fraction of the songs one.
/// </para>
/// </summary>
public sealed record AlbumSummaryDto
{
    /// <summary>
    /// Stable identity, and the <c>?album=</c> URL parameter. For built songs this is the
    /// <b>destination folder directory</b> — the same unit Navidrome groups on (the builder writes
    /// one reconciled MUSICBRAINZ_ALBUMID per folder), so one album name split across releases shows
    /// as the separate cards the player shows. Songs with no destination path fall back to
    /// <see cref="NameKey"/>.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Every destination folder this card covers — <c>[Key]</c> for a plain card, and all of the
    /// merged folders (representative first) for a merged one. Lets a client resolve an
    /// <c>?album=&lt;folder&gt;</c> deep-link pointing at a folder that lost the representative
    /// election, and tells it whether it may pass a single-folder hint to <c>/api/albums/detail</c>.
    /// </summary>
    public required IReadOnlyList<string> FolderKeys { get; init; }

    /// <summary><c>artistLower::titleLower</c> — the name-level identity the merge folds on, and the
    /// legacy deep-link shape older pages still emit.</summary>
    public required string NameKey { get; init; }

    public required string Title { get; init; }
    public required string Artist { get; init; }

    /// <summary>The earliest year the tracks agree on — a deluxe reissue's tracks carry the reissue
    /// year, and the album is still the year it came out.</summary>
    public int? Year { get; init; }

    public required int TrackCount { get; init; }

    /// <summary>Sum of the tracks' durations.</summary>
    public required int DurationSeconds { get; init; }

    /// <summary>Sum of the tracks' file sizes.</summary>
    public required long ByteSize { get; init; }

    /// <summary>First non-null genre encountered.</summary>
    public string? Genre { get; init; }

    /// <summary>First non-null record label encountered.</summary>
    public string? Label { get; init; }

    /// <summary>First non-null catalog number encountered.</summary>
    public string? CatalogNumber { get; init; }

    /// <summary>First non-null barcode / UPC encountered.</summary>
    public string? Upc { get; init; }

    /// <summary>First non-null full release date (ISO string) encountered.</summary>
    public string? ReleaseDate { get; init; }

    /// <summary>First non-null MusicBrainz release id encountered.</summary>
    public string? MusicBrainzReleaseId { get; init; }

    /// <summary>
    /// The first track that has artwork, or null when none does. An id rather than a URL: the cover
    /// endpoint's shape and the same-origin proxy prefix are the client's business, and the Android
    /// client already models it this way.
    /// </summary>
    public int? CoverSongId { get; init; }

    /// <summary>
    /// The most recent added-date over the tracks that are "my music" — the owner asked for them,
    /// rather than album completion adding them because another track from the record was already
    /// owned. Album completion dropping a track into a record owned for years must not pull it back
    /// to the front of "Recently added". An album made <b>entirely</b> of fill falls back to all of
    /// its tracks, so it still carries a date rather than sorting last on null.
    /// </summary>
    public DateTime? AddedAtUtc { get; init; }

    /// <summary>Sum of the tracks' play counts.</summary>
    public required int PlayCount { get; init; }

    /// <summary>The album's tracks, ordered by track number then title.</summary>
    public required IReadOnlyList<int> TrackIds { get; init; }
}
