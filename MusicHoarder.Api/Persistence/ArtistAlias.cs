namespace MusicHoarder.Api.Persistence;

/// <summary>
/// One owner's canonical spelling for an artist-name variant, written by an artist merge
/// ("JAYZ" → "JAY-Z"). Keyed by the variant's <c>TitleNormalizer.NormalizeArtistKey</c> form so any
/// casing/punctuation/diacritic spelling of the same name resolves — but not a longer credit: that
/// key keeps parenthetical and featuring text, so "Nas (featuring AZ)" never resolves through the
/// "nas" row. Consulted by the album-split healer and by enrichment before applying provider
/// spellings, so a merged-away variant can't be silently reintroduced by the next heal pass or
/// re-enrichment.
/// <para>
/// Rows written before the artist key are keyed by the <c>NormalizeForSearch</c> form. For every
/// plain name the two are the same string, so those rows still resolve and a new merge updates
/// them in place (the unique (OwnerUserId, AliasKey) index sees one key); a legacy key that dropped
/// bracketed or feat text is simply never looked up again. No migration re-keys them — see
/// <c>ArtistAliasMap</c> for why there is no search-key fallback either.
/// </para>
/// </summary>
public class ArtistAlias
{
    public int Id { get; set; }
    public Guid OwnerUserId { get; set; }

    /// <summary>Normalized (artist-key form; search form on legacy rows) key of the variant spelling.</summary>
    public required string AliasKey { get; set; }

    /// <summary>The display spelling the owner chose as canonical.</summary>
    public required string CanonicalName { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
