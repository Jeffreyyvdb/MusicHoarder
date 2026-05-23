using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Enrichment.Providers;

/// <summary>
/// Juice WRLD community-tracker provider, backed by the live juicewrldapi.com database
/// (<see cref="JuiceWrldTrackerService"/>). Gated to <see cref="MusicEnricherOptions.TrackerArtistAllowlist"/>
/// and disabled by default (<see cref="MusicEnricherOptions.EnableTrackerProvider"/>). All matching
/// lives in <see cref="CommunityTrackerEnrichmentProvider"/>.
/// </summary>
public sealed class TrackerEnrichmentProvider(
    ITrackerCatalogService catalog,
    IOptions<MusicEnricherOptions> options,
    ILogger<TrackerEnrichmentProvider> logger)
    : CommunityTrackerEnrichmentProvider(catalog, options, logger)
{
    public override string Name => "Tracker";
    public override int Priority => 400;
    protected override IReadOnlyList<string> ArtistAllowlist => Options.TrackerArtistAllowlist;
}
