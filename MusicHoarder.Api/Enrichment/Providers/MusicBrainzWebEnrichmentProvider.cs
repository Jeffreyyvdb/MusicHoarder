using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment.Providers;

/// <summary>
/// Placeholder provider for MusicBrainz web-service matching (ISRC and artist+title search).
/// Actual implementation is tracked by a separate issue.
/// </summary>
public class MusicBrainzWebEnrichmentProvider(
    ILogger<MusicBrainzWebEnrichmentProvider> logger) : IEnrichmentProvider
{
    public string Name => "MusicBrainzWeb";
    public int Priority => 200;

    public bool CanHandle(SongMetadata song) =>
        !string.IsNullOrWhiteSpace(song.Isrc)
        || (!string.IsNullOrWhiteSpace(song.Artist) && !string.IsNullOrWhiteSpace(song.Title));

    public Task<EnrichmentProviderResult?> TryEnrichAsync(SongMetadata song, CancellationToken ct = default)
    {
        logger.LogDebug("MusicBrainzWeb provider not yet implemented; skipping song {SongId}", song.Id);
        return Task.FromResult<EnrichmentProviderResult?>(null);
    }
}
