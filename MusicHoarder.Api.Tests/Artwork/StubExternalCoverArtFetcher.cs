using MusicHoarder.Api.Artwork;

namespace MusicHoarder.Api.Tests.Artwork;

/// <summary>
/// Scriptable fetcher stub. Defaults to "no cover anywhere" so existing source-art tests keep their
/// behavior; tests asserting the external path set <see cref="Result"/> and check <see cref="Calls"/>.
/// </summary>
public sealed class StubExternalCoverArtFetcher : IExternalCoverArtFetcher
{
    public ExternalCoverArtFetchResult Result { get; set; } = new(null, HadTransientFailure: false);
    public ExternalCoverArtFetchResult? SpotifyResult { get; set; }
    public List<ExternalCoverArtQuery> Calls { get; } = [];
    public List<ExternalCoverArtQuery> SpotifyCalls { get; } = [];

    public Task<ExternalCoverArtFetchResult> FetchAsync(ExternalCoverArtQuery query, CancellationToken ct = default)
    {
        Calls.Add(query);
        return Task.FromResult(Result);
    }

    public Task<ExternalCoverArtFetchResult> FetchSpotifyAsync(ExternalCoverArtQuery query, CancellationToken ct = default)
    {
        SpotifyCalls.Add(query);
        return Task.FromResult(SpotifyResult ?? Result);
    }
}
