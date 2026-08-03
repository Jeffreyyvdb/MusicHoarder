namespace MusicHoarder.Api.Persistence;

/// <summary>
/// One owner's canonical spelling for an artist-name variant, written by an artist merge
/// ("JAYZ" → "JAY-Z"). Keyed by the variant's <c>TitleNormalizer.NormalizeForSearch</c> form so any
/// casing/punctuation/diacritic spelling of the same name resolves. Consulted by the album-split
/// healer and by enrichment before applying provider spellings, so a merged-away variant can't be
/// silently reintroduced by the next heal pass or re-enrichment.
/// </summary>
public class ArtistAlias
{
    public int Id { get; set; }
    public Guid OwnerUserId { get; set; }

    /// <summary>Normalized (search-form) key of the variant spelling.</summary>
    public required string AliasKey { get; set; }

    /// <summary>The display spelling the owner chose as canonical.</summary>
    public required string CanonicalName { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
