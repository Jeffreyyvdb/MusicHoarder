using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment.Providers;

/// <summary>
/// Placeholder provider for community tracker matching (unreleased/leak files).
/// Actual implementation is tracked by a separate issue.
/// </summary>
public class TrackerEnrichmentProvider(
    ILogger<TrackerEnrichmentProvider> logger) : IEnrichmentProvider
{
    public string Name => "Tracker";
    public int Priority => 400;

    public bool CanHandle(SongMetadata song) =>
        !string.IsNullOrWhiteSpace(song.Artist) || !string.IsNullOrWhiteSpace(song.Title);

    public Task<EnrichmentProviderResult?> TryEnrichAsync(SongMetadata song, CancellationToken ct = default)
    {
        logger.LogDebug("Tracker provider not yet implemented; skipping song {SongId}", song.Id);
        return Task.FromResult<EnrichmentProviderResult?>(null);
    }
}
