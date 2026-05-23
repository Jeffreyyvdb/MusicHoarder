using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment;

/// <summary>
/// The human-meaningful search term a provider used for a song, for provenance display in the
/// review timeline. Derived from the same <see cref="SongSearchText"/> resolution the providers
/// feed their searches (tags → file path), so the timeline faithfully shows what was queried —
/// e.g. a wrong path-derived title surfaces here as the obviously-wrong term.
/// </summary>
public static class ProviderSearchQuery
{
    /// <summary>
    /// Returns the search term recorded for <paramref name="provider"/> against <paramref name="song"/>,
    /// or null when the provider doesn't do a text search (AcoustID is a fingerprint lookup) or
    /// nothing searchable could be resolved.
    /// </summary>
    public static string? For(EnrichmentProvider provider, SongMetadata song, string? sourceRoot)
    {
        // Fingerprint lookup — there is no text query to show or link.
        if (provider == EnrichmentProvider.AcoustID)
            return null;

        var (artist, title) = SongSearchText.Resolve(song, sourceRoot);

        // Community trackers are single-artist catalogs searched by title only.
        if (provider is EnrichmentProvider.Tracker or EnrichmentProvider.YeTracker)
            return string.IsNullOrWhiteSpace(title) ? null : title!.Trim();

        // Name-based catalogs search on artist + title.
        var combined = string.Join(' ', new[] { artist, title }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim()));
        return combined.Length == 0 ? null : combined;
    }
}
