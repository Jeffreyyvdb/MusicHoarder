namespace MusicHoarder.Api.Persistence;

/// <summary>
/// The audit trail the curation tools leave in <see cref="SongMetadataChange"/>: the
/// <see cref="SongMetadataChange.Source"/> each tool writes under, and the one way they write a row.
/// <para>
/// The spellings are stored on every row, and the readers find past actions by them: the dedup history
/// lists and reverts merges, splits and heals; the History feed names them; the credit repair defers to
/// album-level writers and undoes what a merge or split damaged. Never rename one — rows written under
/// the old spelling would silently drop out of all three. Enrichment is not here: it audits under the
/// winning provider's name (<c>EnrichmentOrchestrator</c>), and it records proposals as well as writes.
/// </para>
/// </summary>
public static class SongChangeAudit
{
    /// <summary>A user's artist merge (<c>ArtistDuplicateService.MergeAsync</c>).</summary>
    public const string ArtistMerge = "artist-merge";

    /// <summary>A user's split of a combined artist credit (<c>ArtistDuplicateService.SplitCreditAsync</c>).</summary>
    public const string ArtistCreditSplit = "artist-credit-split";

    /// <summary>A user's album merge (<c>POST /api/library/albums/merge</c>).</summary>
    public const string AlbumMerge = "album-merge";

    /// <summary>The split-album heal (<c>AlbumSplitHealer</c>, which still writes its rows by hand under
    /// its own copy of this spelling).</summary>
    public const string AlbumIdentityHeal = "album-identity-heal";

    /// <summary>The discrete-artists backfill from provider attempts (<c>ArtistCreditHealer</c>).</summary>
    public const string ArtistCreditHeal = "artist-credit-heal";

    /// <summary>Consolidation onto a fetched canonical tracklist (<c>CanonicalAlbumConsolidator</c>).</summary>
    public const string CanonicalAlbum = "canonical-album";

    /// <summary>The credit repair's collab album artist moved to the lead (<c>ArtistCreditRepairHealer</c>).</summary>
    public const string AlbumArtistRepair = "album-artist-repair";

    /// <summary>The credit repair restoring a display credit an artist merge folded into its lead.</summary>
    public const string ArtistMergeRepair = "artist-merge-repair";

    /// <summary>The credit repair dropping or undoing what a credit split wrote into Artists.</summary>
    public const string CreditSplitRepair = "credit-split-repair";

    /// <summary>The credit repair restoring a featuring credit a merge alias turned into its lead.</summary>
    public const string ArtistAliasRepair = "artist-alias-repair";

    /// <summary>
    /// Records one field a curation action changed, as applied. Every row of one action takes the same
    /// <paramref name="batchStampUtc"/>: (<paramref name="source"/>, stamp) is how the dedup history finds
    /// the action again to list and revert it, so a second stamp would split one action in two.
    /// </summary>
    public static void Record(
        MusicHoarderDbContext db, string source, DateTime batchStampUtc,
        int songId, string fieldName, string? oldValue, string? newValue) =>
        db.SongMetadataChanges.Add(new SongMetadataChange
        {
            SongId = songId,
            FieldName = fieldName,
            OldValue = oldValue,
            NewValue = newValue,
            Source = source,
            Confidence = 1.0,
            CreatedAtUtc = batchStampUtc,
            AppliedAtUtc = batchStampUtc,
        });
}
