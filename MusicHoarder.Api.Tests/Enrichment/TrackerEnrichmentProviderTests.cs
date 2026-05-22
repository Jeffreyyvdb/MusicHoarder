using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Enrichment.Providers;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

public class TrackerEnrichmentProviderTests
{
    [Fact]
    public async Task ExactTitleMatch_ForAllowlistedArtist_ReturnsMatched()
    {
        var svc = new StubCatalog
        {
            Search = _ =>
            [
                new TrackerSong(1, "Lucid Dreams", [], "released", "gbgr", "Juice WRLD", "Nick Mira", 239, 2018),
            ],
        };
        var provider = Create(svc);
        var song = Song(artist: "Juice WRLD", title: "Lucid Dreams", durationSec: 239);

        var outcome = await provider.TryEnrichAsync(song);

        var matched = Assert.IsType<ProviderMatched>(outcome);
        Assert.Equal(EnrichmentStatus.Matched, matched.Result.RecommendedStatus);
        Assert.Equal("Lucid Dreams", matched.Result.Title);
        Assert.Equal("Juice WRLD", matched.Result.Artist);
        Assert.Equal("gbgr", matched.Result.Album);
        Assert.Equal(2018, matched.Result.Year);
        Assert.Equal("Tracker", matched.Result.MatchedBy);
        Assert.Contains("category:released", matched.Result.MatchWarnings);
        Assert.Equal(1, svc.SearchCalls);
    }

    [Fact]
    public async Task AliasTitleMatch_ViaTrackTitles_ReturnsMatched()
    {
        // The canonical name is a cryptic leak handle; the human-readable title only matches an alias.
        var svc = new StubCatalog
        {
            Search = _ =>
            [
                new TrackerSong(2, "2MININHELL", ["2MININHELL", "2MININHELL (Pt. 1)", "2 Minutes In Hell (Pt. 1)"],
                    "unreleased", "jute", "JuiceTheKidd", "J Knight", 222, 2016),
            ],
        };
        var provider = Create(svc);
        var song = Song(artist: "Juice WRLD", title: "2 Minutes In Hell", durationSec: 222);

        var outcome = await provider.TryEnrichAsync(song);

        var matched = Assert.IsType<ProviderMatched>(outcome);
        Assert.Equal("2MININHELL", matched.Result.Title);
        Assert.Contains("category:unreleased", matched.Result.MatchWarnings);
    }

    [Fact]
    public async Task UntaggedArtist_FallsBackToTrackerCredit()
    {
        var svc = new StubCatalog
        {
            Search = _ =>
            [
                new TrackerSong(3, "Lucid Dreams", [], "released", "gbgr", "JuiceTheKidd", null, 239, 2018),
            ],
        };
        var provider = Create(svc);
        // No tag artist; path supplies the allowlisted "Juice WRLD" folder so the gate still opens.
        var song = Song(title: null, durationSec: 239);
        song.SourcePath = "/s/Juice WRLD/Goodbye/Lucid Dreams.mp3";

        var outcome = await provider.TryEnrichAsync(song);

        var matched = Assert.IsType<ProviderMatched>(outcome);
        Assert.Equal("JuiceTheKidd", matched.Result.Artist);
    }

    [Fact]
    public async Task TitleMismatch_BelowMinConfidence_ReturnsNoMatchWithCandidate()
    {
        var svc = new StubCatalog
        {
            Search = _ =>
            [
                new TrackerSong(4, "Totally Different Track", [], "released", "era", "Juice WRLD", null, 200, 2019),
            ],
        };
        var provider = Create(svc);
        var song = Song(artist: "Juice WRLD", title: "Lucid Dreams", durationSec: 239);

        var outcome = await provider.TryEnrichAsync(song);

        var noMatch = Assert.IsType<ProviderNoMatch>(outcome);
        Assert.NotNull(noMatch.BestCandidate);
        Assert.Contains("title_mismatch", noMatch.BestCandidate!.MatchWarnings);
    }

    [Fact]
    public async Task DurationMismatch_DowngradesToNeedsReview()
    {
        var svc = new StubCatalog
        {
            Search = _ =>
            [
                new TrackerSong(5, "Lucid Dreams", [], "released", "gbgr", "Juice WRLD", null, 120, 2018),
            ],
        };
        var provider = Create(svc);
        var song = Song(artist: "Juice WRLD", title: "Lucid Dreams", durationSec: 239); // ~2min apart

        var outcome = await provider.TryEnrichAsync(song);

        var matched = Assert.IsType<ProviderMatched>(outcome);
        Assert.Equal(EnrichmentStatus.NeedsReview, matched.Result.RecommendedStatus);
        Assert.Contains("duration_mismatch", matched.Result.MatchWarnings);
    }

    [Fact]
    public async Task NoSearchResults_ReturnsNoMatch()
    {
        var svc = new StubCatalog { Search = _ => [] };
        var provider = Create(svc);
        var song = Song(artist: "Juice WRLD", title: "Nonexistent Leak");

        var outcome = await provider.TryEnrichAsync(song);

        Assert.IsType<ProviderNoMatch>(outcome);
    }

    [Fact]
    public async Task RateLimited_ReturnsRateLimitedOutcome()
    {
        var svc = new StubCatalog { Throw = () => new ProviderRateLimitedException(TimeSpan.FromSeconds(7)) };
        var provider = Create(svc);
        var song = Song(artist: "Juice WRLD", title: "Lucid Dreams");

        var outcome = await provider.TryEnrichAsync(song);

        var rl = Assert.IsType<ProviderRateLimited>(outcome);
        Assert.Equal(7, rl.RetryAfter.TotalSeconds);
    }

    [Fact]
    public void CanHandle_NonAllowlistedArtist_IsFalse()
    {
        var provider = Create(new StubCatalog());
        var song = Song(artist: "Taylor Swift", title: "Anti-Hero");

        Assert.False(provider.CanHandle(song));
    }

    [Fact]
    public void CanHandle_AllowlistedArtist_IsTrue()
    {
        var provider = Create(new StubCatalog());
        var song = Song(artist: "Juice WRLD", title: "Lucid Dreams");

        Assert.True(provider.CanHandle(song));
    }

    // --- helpers ---

    private static TrackerEnrichmentProvider Create(ITrackerCatalogService svc) =>
        new(svc,
            Microsoft.Extensions.Options.Options.Create(
                new MusicEnricherOptions { SourceDirectory = "/s", DestinationDirectory = "/d" }),
            NullLogger<TrackerEnrichmentProvider>.Instance);

    private static SongMetadata Song(string? artist = null, string? title = null, int? durationSec = null) => new()
    {
        OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
        SourcePath = "/x.mp3",
        FileName = "x.mp3",
        Extension = ".mp3",
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Artist = artist,
        Title = title,
        DurationSeconds = durationSec,
    };

    private sealed class StubCatalog : ITrackerCatalogService
    {
        public Func<string, IReadOnlyList<TrackerSong>>? Search { get; set; }
        public Func<Exception>? Throw { get; set; }
        public int SearchCalls { get; private set; }

        public Task<IReadOnlyList<TrackerSong>> SearchAsync(string title, CancellationToken ct = default)
        {
            SearchCalls++;
            if (Throw is not null)
                throw Throw();
            return Task.FromResult(Search?.Invoke(title) ?? []);
        }
    }
}
