using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Enrichment.Providers;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

public class YeTrackerEnrichmentProviderTests
{
    [Fact]
    public async Task ExactTitleMatch_ForKanye_ReturnsMatched()
    {
        var provider = Create(new TrackerSong(1, "Famous", [], "released", "The Life Of Pablo", "Kanye West", null, 196, 2016));
        var song = Song(artist: "Kanye West", title: "Famous", durationSec: 196);

        var outcome = await provider.TryEnrichAsync(song);

        var matched = Assert.IsType<ProviderMatched>(outcome);
        Assert.Equal(EnrichmentStatus.Matched, matched.Result.RecommendedStatus);
        Assert.Equal("Famous", matched.Result.Title);
        Assert.Equal("Kanye West", matched.Result.Artist);
        Assert.Equal("The Life Of Pablo", matched.Result.Album);
        Assert.Equal(2016, matched.Result.Year);
        Assert.Equal("YeTracker", matched.Result.MatchedBy);
        Assert.Contains("category:released", matched.Result.MatchWarnings);
    }

    [Fact]
    public async Task AliasTitleMatch_ViaTrackTitles_ReturnsMatched()
    {
        var provider = Create(new TrackerSong(2, "Wolves [V3]", ["Wolves"], "unreleased", "TLOP", "Kanye West", null, 200, 2015));
        var song = Song(artist: "Ye", title: "Wolves", durationSec: 200);

        var outcome = await provider.TryEnrichAsync(song);

        var matched = Assert.IsType<ProviderMatched>(outcome);
        Assert.Equal("Wolves [V3]", matched.Result.Title);
    }

    [Fact]
    public async Task DurationMismatch_DowngradesToNeedsReview()
    {
        var provider = Create(new TrackerSong(3, "Famous", [], "released", "TLOP", "Kanye West", null, 60, 2016));
        var song = Song(artist: "Kanye West", title: "Famous", durationSec: 196); // ~2min apart

        var outcome = await provider.TryEnrichAsync(song);

        var matched = Assert.IsType<ProviderMatched>(outcome);
        Assert.Equal(EnrichmentStatus.NeedsReview, matched.Result.RecommendedStatus);
        Assert.Contains("duration_mismatch", matched.Result.MatchWarnings);
    }

    [Fact]
    public async Task NoSearchResults_ReturnsNoMatch()
    {
        var provider = Create(new TrackerSong(4, "Famous", [], "released", "TLOP", "Kanye West", null, 196, 2016));
        var song = Song(artist: "Kanye West", title: "Some Unknown Leak");

        Assert.IsType<ProviderNoMatch>(await provider.TryEnrichAsync(song));
    }

    [Fact]
    public void CanHandle_NonKanyeArtist_IsFalse()
    {
        var provider = Create();
        Assert.False(provider.CanHandle(Song(artist: "Taylor Swift", title: "Anti-Hero")));
    }

    [Theory]
    [InlineData("Kanye West")]
    [InlineData("Ye")]
    [InlineData("Kanye")]
    public void CanHandle_KanyeAliases_AreTrue(string artist)
    {
        var provider = Create();
        Assert.True(provider.CanHandle(Song(artist: artist, title: "Famous")));
    }

    // --- helpers ---

    private static YeTrackerEnrichmentProvider Create(params TrackerSong[] songs)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/s",
            DestinationDirectory = "/d",
            EnableYeTrackerProvider = true,
        });
        var catalog = new YeTrackerCatalogService(songs, options);
        return new YeTrackerEnrichmentProvider(catalog, options, NullLogger<YeTrackerEnrichmentProvider>.Instance);
    }

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
}
