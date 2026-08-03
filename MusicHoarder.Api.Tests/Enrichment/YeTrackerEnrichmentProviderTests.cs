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
    public async Task ManyVersions_PicksTheOneClosestInLength()
    {
        // A single song with many same-title versions (each carries the version-stripped alias, so
        // they all tie on title score). Length must break the tie and pick the closest version.
        var provider = Create(
            new TrackerSong(1, "LA Monster [V2]", ["LA Monster"], "unreleased", "JESUS IS KING", "Kanye West", null, 130, 2019),
            new TrackerSong(2, "LA Monster [V4]", ["LA Monster"], "unreleased", "JESUS IS KING", "Kanye West", null, 199, 2019),
            new TrackerSong(3, "LA Monster [V5]", ["LA Monster"], "unreleased", "JESUS IS KING", "Kanye West", null, 179, 2019));
        var song = Song(artist: "Kanye West", title: "LA Monster", durationSec: 198); // ~V4

        var outcome = await provider.TryEnrichAsync(song);

        var matched = Assert.IsType<ProviderMatched>(outcome);
        Assert.Equal("LA Monster [V4]", matched.Result.Title);
    }

    [Fact]
    public async Task ManyVersions_LengthlessCandidate_LosesToLengthMatch()
    {
        var provider = Create(
            new TrackerSong(1, "LA Monster [V1]", ["LA Monster"], "unreleased", "JESUS IS KING", "Kanye West", null, null, 2019),
            new TrackerSong(2, "LA Monster [V4]", ["LA Monster"], "unreleased", "JESUS IS KING", "Kanye West", null, 199, 2019));
        var song = Song(artist: "Kanye West", title: "LA Monster", durationSec: 200);

        var outcome = await provider.TryEnrichAsync(song);

        var matched = Assert.IsType<ProviderMatched>(outcome);
        Assert.Equal("LA Monster [V4]", matched.Result.Title);
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

    // --- availability ---

    [Fact]
    public async Task NeverLeakedCandidate_IsNotMatchable()
    {
        // "Confirmed" documents that the song exists, not that a file circulates — so nothing on
        // disk can be it, however well the title agrees.
        var provider = Create(Track("Assassinate Rhymes", availability: "Confirmed"));
        var song = Song(artist: "Kanye West", title: "Assassinate Rhymes");

        Assert.IsType<ProviderNoMatch>(await provider.TryEnrichAsync(song));
    }

    [Fact]
    public async Task NeverLeakedCandidate_LosesToTheOneThatCirculates()
    {
        var provider = Create(
            Track("Bad News [V1]", ["Bad News"], availability: "Rumored"),
            Track("Bad News [V2]", ["Bad News"], availability: "OG File"));
        var song = Song(artist: "Kanye West", title: "Bad News");

        var matched = Assert.IsType<ProviderMatched>(await provider.TryEnrichAsync(song));
        Assert.Equal("Bad News [V2]", matched.Result.Title);
    }

    [Fact]
    public async Task WithoutLengths_FullCirculationBeatsSnippet()
    {
        // Neither candidate has a length, so the duration tiebreak can't decide; a library file is
        // far likelier to be the version that circulates in full than a snippet.
        var provider = Create(
            Track("Someday [V3]", ["Someday"], availability: "Snippet"),
            Track("Someday [V8]", ["Someday"], availability: "Full"));
        var song = Song(artist: "Kanye West", title: "Someday");

        var matched = Assert.IsType<ProviderMatched>(await provider.TryEnrichAsync(song));
        Assert.Equal("Someday [V8]", matched.Result.Title);
    }

    // --- version markers ---

    [Fact]
    public async Task VersionMarker_PicksTheMatchingVersion_WhenLengthsAreUnknown()
    {
        var provider = Create(
            Track("Higher [V2]", ["Higher"]),
            Track("Higher [V7]", ["Higher"]),
            Track("Higher [V11]", ["Higher"]));
        var song = Song(artist: "Kanye West", title: "Higher [V7]");

        var matched = Assert.IsType<ProviderMatched>(await provider.TryEnrichAsync(song));
        Assert.Equal("Higher [V7]", matched.Result.Title);
        Assert.DoesNotContain("version_number_mismatch", matched.Result.MatchWarnings);
    }

    [Fact]
    public async Task VersionMarker_Disagreement_IsWarnedAndPenalised()
    {
        var provider = Create(Track("Higher [V11]", ["Higher"]));
        var song = Song(artist: "Kanye West", title: "Higher [V2]");

        var outcome = await provider.TryEnrichAsync(song);

        var result = outcome switch
        {
            ProviderMatched m => m.Result,
            ProviderNoMatch { BestCandidate: { } candidate } => candidate,
            _ => throw new Xunit.Sdk.XunitException($"unexpected outcome {outcome.GetType().Name}"),
        };
        Assert.Contains("version_number_mismatch", result!.MatchWarnings);
        Assert.NotEqual(EnrichmentStatus.Matched, result.RecommendedStatus);
    }

    // --- OG filenames ---

    [Fact]
    public async Task OgFilename_IdentifiesAnUntaggedLeak()
    {
        // The classic untagged leak: no tags at all, and the filename is the tracker's OG filename.
        var provider = Create(Track("Diamonds [V4]", ogFilenames: ["DIAMONDSAD122_01"]));
        var song = Song(
            artist: "Kanye West",
            sourcePath: "/s/Kanye West/Late Registration/DIAMONDSAD122_01.mp3",
            fileName: "DIAMONDSAD122_01.mp3");

        var matched = Assert.IsType<ProviderMatched>(await provider.TryEnrichAsync(song));
        Assert.Equal("Diamonds [V4]", matched.Result.Title);
        Assert.Contains("matched_via_og_filename", matched.Result.MatchWarnings);
    }

    [Fact]
    public async Task OgFilename_DoesNotOverrideAStrongerTitleMatch()
    {
        var provider = Create(Track("Diamonds [V4]", ogFilenames: ["DIAMONDSAD122_01"]));
        var song = Song(artist: "Kanye West", title: "Diamonds [V4]");

        var matched = Assert.IsType<ProviderMatched>(await provider.TryEnrichAsync(song));
        Assert.DoesNotContain("matched_via_og_filename", matched.Result.MatchWarnings);
    }

    // --- AI fakes ---

    [Fact]
    public async Task AiGeneratedCandidate_IsSurfacedButNeverMatched()
    {
        var provider = Create(Track("PEACE AND QUIET [V12]", availability: "OG File", aiGenerated: true));
        var song = Song(artist: "Kanye West", title: "PEACE AND QUIET [V12]");

        var outcome = await provider.TryEnrichAsync(song);

        var matched = Assert.IsType<ProviderMatched>(outcome);
        Assert.Contains("ai_generated", matched.Result.MatchWarnings);
        Assert.Equal(EnrichmentStatus.NeedsReview, matched.Result.RecommendedStatus);
    }

    [Fact]
    public async Task AiGeneratedCandidate_LosesToTheRealRecording()
    {
        // The fake carries the same title, era and version as the song it imitates.
        var provider = Create(
            Track("PEACE AND QUIET [V12]", availability: "OG File", aiGenerated: true),
            Track("PEACE AND QUIET [V12]", availability: "OG File"));
        var song = Song(artist: "Kanye West", title: "PEACE AND QUIET [V12]");

        var matched = Assert.IsType<ProviderMatched>(await provider.TryEnrichAsync(song));
        Assert.DoesNotContain("ai_generated", matched.Result.MatchWarnings);
        Assert.Equal(EnrichmentStatus.Matched, matched.Result.RecommendedStatus);
    }

    // --- released-track classification ---

    [Theory]
    [InlineData("Feature")]
    [InlineData("Production")]
    public async Task FeatureOrProductionCredit_DoesNotBorrowTheEraAsAlbum(string trackType)
    {
        // "World Domination" is a Grav track Ye produced; the era is Ye's career period, not the
        // album the song is on, so attributing it would file the track under a release it isn't on.
        var provider = Create(Track("World Domination", availability: "Full", trackType: trackType));
        var song = Song(artist: "Kanye West", title: "World Domination");

        var matched = Assert.IsType<ProviderMatched>(await provider.TryEnrichAsync(song));
        Assert.Null(matched.Result.Album);
    }

    [Fact]
    public async Task AlbumTrack_StillTakesTheEraAsAlbum()
    {
        var provider = Create(Track("Famous", availability: "Full", trackType: "Album Track"));
        var song = Song(artist: "Kanye West", title: "Famous");

        var matched = Assert.IsType<ProviderMatched>(await provider.TryEnrichAsync(song));
        Assert.Equal("Donda", matched.Result.Album);
    }

    [Fact]
    public async Task SpotifyId_IsCarriedThroughFromTheTracker()
    {
        var provider = Create(Track("Famous", availability: "Full", spotifyId: "0o2r7L42B1clJULbC4xRzM"));
        var song = Song(artist: "Kanye West", title: "Famous");

        var matched = Assert.IsType<ProviderMatched>(await provider.TryEnrichAsync(song));
        Assert.Equal("0o2r7L42B1clJULbC4xRzM", matched.Result.SpotifyId);
    }

    [Fact]
    public async Task UntaggedFile_GetsTheGuestCreditInItsArtist()
    {
        var provider = Create(Track("Famous", availability: "Full", featured: "Rihanna & Swizz Beatz"));
        var song = Song(sourcePath: "/s/Kanye West/Donda/Famous.mp3", fileName: "Famous.mp3");

        var matched = Assert.IsType<ProviderMatched>(await provider.TryEnrichAsync(song));
        Assert.Equal("Kanye West feat. Rihanna & Swizz Beatz", matched.Result.Artist);
        // The tracker only publishes a combined credit, so the discrete frame stays empty.
        Assert.Null(matched.Result.Artists);
    }

    // --- artist allowlist ---

    [Theory]
    [InlineData("Yeat")]                  // the real regression: "Yeat, EsDeeKid" got a YeTracker match
    [InlineData("Yeat, EsDeeKid")]
    [InlineData("Yeat & Drake")]
    [InlineData("Yebba")]
    [InlineData("Yeule")]
    [InlineData("Yeah Yeah Yeahs")]
    [InlineData("Ye Ali")]
    public void CanHandle_ArtistMerelyContainingYe_IsFalse(string artist)
    {
        // FuzzyTextMatch.Ratio is a weighted ratio: against the two-letter alias "Ye" every one of
        // these scores 90 (partial-ratio fallback), clearing the 85 threshold. Short allowlist
        // entries must therefore match exactly — otherwise the tracker not only enriches an
        // unrelated artist, it also stamps the song as unreleased.
        var provider = Create();
        Assert.False(provider.CanHandle(Song(artist: artist, title: "Made It On Our Own")));
    }

    [Theory]
    [InlineData("Ye, Ty Dolla $ign")]
    [InlineData("Kanye West, Ty Dolla $ign")]
    [InlineData("Kanye West & Jay-Z")]
    [InlineData("Ye feat. Charlie Wilson")]
    [InlineData("Kanyé West")]
    public void CanHandle_KanyeCollaborations_AreTrue(string artist)
    {
        var provider = Create();
        Assert.True(provider.CanHandle(Song(artist: artist, title: "Famous")));
    }

    // --- helpers ---

    /// <summary>A yetracker-shaped candidate: no length (most leaks have none) unless given one.</summary>
    private static TrackerSong Track(
        string name,
        string[]? aliases = null,
        string? availability = null,
        double? durationSeconds = null,
        string[]? ogFilenames = null,
        bool aiGenerated = false,
        string? trackType = null,
        string? spotifyId = null,
        string? featured = null) =>
        new(0, name, aliases ?? [], "unreleased", "Donda", "Kanye West", null, durationSeconds, null,
            Availability: availability, OgFilenames: ogFilenames ?? [],
            Version: ParseVersion(name),
            Featured: featured, SpotifyId: spotifyId, TrackType: trackType, IsAiGenerated: aiGenerated);

    private static int? ParseVersion(string name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(name, @"\[[Vv](\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

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

    private static SongMetadata Song(
        string? artist = null,
        string? title = null,
        int? durationSec = null,
        string sourcePath = "/x.mp3",
        string fileName = "x.mp3") => new()
    {
        OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
        SourcePath = sourcePath,
        FileName = fileName,
        Extension = ".mp3",
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Artist = artist,
        Title = title,
        DurationSeconds = durationSec,
    };
}
