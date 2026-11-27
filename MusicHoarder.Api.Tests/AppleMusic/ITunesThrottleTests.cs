using MusicHoarder.Api.AppleMusic;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.AppleMusic;

[Collection(ITunesThrottleCollection.Name)]
public sealed class ITunesThrottleTests : IDisposable
{
    public ITunesThrottleTests() => ITunesThrottle.ResetBackoffForTests();

    public void Dispose() => ITunesThrottle.ResetBackoffForTests();

    [Fact]
    public async Task A_permit_granted_inside_a_backoff_window_is_not_used()
    {
        ITunesThrottle.ExtendBackoff(TimeSpan.FromSeconds(30));

        await Assert.ThrowsAsync<ProviderRateLimitedException>(async () =>
            await ITunesThrottle.AcquireAsync(new MusicEnricherOptions { AppleMusicApiRequestsPerMinute = 30 }, CancellationToken.None));
    }
}
