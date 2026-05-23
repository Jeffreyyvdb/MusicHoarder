using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Enrichment.Providers;

/// <summary>
/// Kanye West "yetracker" community-tracker provider, backed by the local
/// <see cref="YeTrackerCatalogService"/> (scraped tracker normalized to a committed JSON catalog).
/// Gated to <see cref="MusicEnricherOptions.YeTrackerArtistAllowlist"/> and disabled by default
/// (<see cref="MusicEnricherOptions.EnableYeTrackerProvider"/>). All matching lives in
/// <see cref="CommunityTrackerEnrichmentProvider"/>. Depends on the concrete catalog (not the
/// <see cref="ITrackerCatalogService"/> interface) so DI doesn't confuse it with the Juice WRLD one.
/// </summary>
public sealed class YeTrackerEnrichmentProvider(
    YeTrackerCatalogService catalog,
    IOptions<MusicEnricherOptions> options,
    ILogger<YeTrackerEnrichmentProvider> logger)
    : CommunityTrackerEnrichmentProvider(catalog, options, logger)
{
    public override string Name => "YeTracker";
    public override int Priority => 410;
    protected override IReadOnlyList<string> ArtistAllowlist => Options.YeTrackerArtistAllowlist;
}
