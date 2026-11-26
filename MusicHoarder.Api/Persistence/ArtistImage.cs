namespace MusicHoarder.Api.Persistence;

/// <summary>
/// Cached artist portrait lookup, keyed by the normalized artist name. Catalog-style (no per-user
/// filter): an artist's portrait is the same for every tenant. A row with a null
/// <see cref="ImageUrl"/> is a negative cache — no provider had a verified portrait when
/// <see cref="FetchedAtUtc"/> was stamped; it's retried after
/// <c>MusicEnricher:ArtistImageNotFoundRetryDays</c>.
/// </summary>
public class ArtistImage
{
    public int Id { get; set; }

    /// <summary>TitleNormalizer.NormalizeForDedup of the artist name. Unique.</summary>
    public string NormalizedName { get; set; } = string.Empty;

    /// <summary>The display spelling the lookup was first made with (diagnostics only).</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Portrait CDN URL sized for grid/list tiles (~300–500px edge); null = no provider had a
    /// verified portrait (negative cache).
    /// </summary>
    public string? ImageUrl { get; set; }

    /// <summary>
    /// The largest available portrait (hero/full-screen views), when the provider offers more
    /// than one size. Null for rows cached before this column existed — those serve
    /// <see cref="ImageUrl"/> everywhere.
    /// </summary>
    public string? ImageUrlLarge { get; set; }

    /// <summary>Provider the portrait came from: <c>spotify</c> | <c>deezer</c>; null when not found.</summary>
    public string? Source { get; set; }

    public DateTime FetchedAtUtc { get; set; }
}
