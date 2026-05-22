using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Enrichment.Providers;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

/// <summary>
/// Opt-in smoke tests that hit the real juicewrldapi.com API to verify the live contract +
/// mapping (which the unit tests stub out). Inert unless <c>MH_TRACKER_LIVE</c> is set, so CI
/// never depends on a third-party endpoint. Run locally with:
/// <c>MH_TRACKER_LIVE=1 dotnet test --filter "FullyQualifiedName~Live"</c>
/// </summary>
public class JuiceWrldTrackerLiveSmokeTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("MH_TRACKER_LIVE") is not null;

    [Fact]
    public async Task Service_SearchesRealApi_AndMapsKnownSong()
    {
        if (!Enabled)
            return;

        var service = new JuiceWrldTrackerService(
            new HttpClient
            {
                BaseAddress = new Uri("https://juicewrldapi.com/juicewrld/"),
                Timeout = TimeSpan.FromSeconds(30),
            },
            Microsoft.Extensions.Options.Options.Create(
                new MusicEnricherOptions { SourceDirectory = "/s", DestinationDirectory = "/d" }),
            NullLogger<JuiceWrldTrackerService>.Instance);

        var results = await service.SearchAsync("Lucid Dreams");

        Assert.NotEmpty(results);
        // The live DB has many "Lucid Dreams" variants (forget me / remix / v2 / sessions); just
        // confirm the search + mapping surfaced the canonical recording with sane metadata.
        Assert.Contains(results, r =>
            r.Name.Contains("Lucid Dreams", StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.CreditedArtists, "Juice WRLD", StringComparison.OrdinalIgnoreCase)
            && r.Year == 2017);
        Assert.All(results, r => Assert.True(r.DurationSeconds is null or > 0));
    }

    [Fact]
    public async Task Provider_EnrichesAgainstRealApi_ForJuiceWrldSong()
    {
        if (!Enabled)
            return;

        var service = new JuiceWrldTrackerService(
            new HttpClient
            {
                BaseAddress = new Uri("https://juicewrldapi.com/juicewrld/"),
                Timeout = TimeSpan.FromSeconds(30),
            },
            Microsoft.Extensions.Options.Options.Create(
                new MusicEnricherOptions { SourceDirectory = "/s", DestinationDirectory = "/d" }),
            NullLogger<JuiceWrldTrackerService>.Instance);

        var provider = new TrackerEnrichmentProvider(
            service,
            Microsoft.Extensions.Options.Options.Create(
                new MusicEnricherOptions { SourceDirectory = "/s", DestinationDirectory = "/d" }),
            NullLogger<TrackerEnrichmentProvider>.Instance);

        var song = new SongMetadata
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            SourcePath = "/s/Juice WRLD/Goodbye & Good Riddance/Lucid Dreams.mp3",
            FileName = "Lucid Dreams.mp3",
            Extension = ".mp3",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = "Juice WRLD",
            Title = "Lucid Dreams",
        };

        Assert.True(provider.CanHandle(song));

        var outcome = await provider.TryEnrichAsync(song);

        var matched = Assert.IsType<ProviderMatched>(outcome);
        Assert.Contains("Lucid Dreams", matched.Result.Title!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Tracker", matched.Result.MatchedBy);
    }
}
