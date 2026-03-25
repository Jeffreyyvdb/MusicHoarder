using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment.Providers;

/// <summary>
/// Placeholder provider for Spotify API matching (artist+title, optional ISRC verification).
/// Actual implementation is tracked by a separate issue.
/// </summary>
public class SpotifyApiEnrichmentProvider(
    ILogger<SpotifyApiEnrichmentProvider> logger) : IEnrichmentProvider
{
    public string Name => "SpotifyAPI";
    public int Priority => 300;

    public bool CanHandle(SongMetadata song) =>
        !string.IsNullOrWhiteSpace(song.Artist) && !string.IsNullOrWhiteSpace(song.Title);

    public Task<EnrichmentProviderResult?> TryEnrichAsync(SongMetadata song, CancellationToken ct = default)
    {
        logger.LogDebug("SpotifyAPI provider not yet implemented; skipping song {SongId}", song.Id);
        return Task.FromResult<EnrichmentProviderResult?>(null);
    }
}
