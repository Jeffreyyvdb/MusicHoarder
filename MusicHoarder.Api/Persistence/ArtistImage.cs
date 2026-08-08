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

    /// <summary>Portrait CDN URL; null = no provider had a verified portrait (negative cache).</summary>
    public string? ImageUrl { get; set; }

    /// <summary>Provider the portrait came from: <c>deezer</c> | <c>spotify</c>; null when not found.</summary>
    public string? Source { get; set; }

    public DateTime FetchedAtUtc { get; set; }
}
