using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

public class MetadataMergerTests
{
    private const double AutoUpgrade = 0.96;

    [Fact]
    public void EmptyExisting_IsFilled()
    {
        var song = Song(artist: null, title: "Title");
        var changes = Merge(song, Winner(artist: "Daft Punk", title: "Title"), confidence: 0.92, providers: 1);

        Assert.Equal("Daft Punk", song.Artist);
        Assert.Contains(changes, c => c is { Field: "Artist", Applied: true });
    }

    [Fact]
    public void LowQualityExisting_IsReplaced_EvenSolo()
    {
        var song = Song(artist: "Artist", title: "Track 03");
        var changes = Merge(song, Winner(artist: "Artist", title: "Real Title"), confidence: 0.90, providers: 1);

        Assert.Equal("Real Title", song.Title);
        Assert.Contains(changes, c => c is { Field: "Title", Applied: true });
    }

    [Fact]
    public void GoodExisting_DiffersAtLowConsensus_IsProposedNotApplied()
    {
        var song = Song(artist: "Beyoncé", title: "Halo");
        var changes = Merge(song, Winner(artist: "Beyonce Knowles", title: "Halo"), confidence: 0.92, providers: 1);

        // Curated value preserved; the change is recorded as a proposal for review.
        Assert.Equal("Beyoncé", song.Artist);
        Assert.Contains(changes, c => c is { Field: "Artist", Applied: false });
    }

    [Fact]
    public void GoodExisting_DiffersAtHighConsensus_IsUpgraded()
    {
        var song = Song(artist: "Beyonce", title: "Halo");
        var changes = Merge(song, Winner(artist: "Beyoncé Knowles-Carter", title: "Halo"), confidence: 0.98, providers: 2);

        Assert.Equal("Beyoncé Knowles-Carter", song.Artist);
        Assert.Contains(changes, c => c is { Field: "Artist", Applied: true });
    }

    [Fact]
    public void NormalizedEqual_IsNoOp()
    {
        var song = Song(artist: "Beyoncé", title: "Halo");
        var changes = Merge(song, Winner(artist: "Beyonce", title: "Halo"), confidence: 0.98, providers: 2);

        // "Beyoncé" vs "Beyonce" normalize equal → keep the curated form, no change recorded.
        Assert.Equal("Beyoncé", song.Artist);
        Assert.DoesNotContain(changes, c => c.Field == "Artist");
    }

    [Fact]
    public void Identifiers_AreAlwaysAttached()
    {
        var song = Song(artist: "Beyoncé", title: "Halo");
        Merge(song, Winner(artist: "Beyonce Knowles", title: "Halo", spotifyId: "spot-1", isrc: "USXXX", mbid: "mb-1"),
            confidence: 0.90, providers: 1);

        Assert.Equal("spot-1", song.SpotifyId);
        Assert.Equal("USXXX", song.Isrc);
        Assert.Equal("mb-1", song.MusicBrainzId);
        Assert.Equal(EnrichmentStatus.Matched, song.EnrichmentStatus);
    }

    [Fact]
    public void Year_FilledWhenEmpty_ProposedWhenConflictLowConsensus()
    {
        var song = Song(artist: "A", title: "T");
        song.Year = 1999;
        var changes = Merge(song, Winner(artist: "A", title: "T", year: 2001), confidence: 0.92, providers: 1);

        Assert.Equal(1999, song.Year); // curated year preserved at low consensus
        Assert.Contains(changes, c => c is { Field: "Year", Applied: false });
    }

    [Fact]
    public void CapturesOriginalMetadata()
    {
        var song = Song(artist: "Track 01", title: "Track 03");
        Merge(song, Winner(artist: "Real Artist", title: "Real Title"), confidence: 0.92, providers: 1);

        Assert.True(song.OriginalMetadataCaptured);
        Assert.Equal("Track 01", song.OriginalArtist);
    }

    // --- helpers ---

    private static IReadOnlyList<MetadataMerger.FieldChange> Merge(
        SongMetadata song, EnrichmentProviderResult winner, double confidence, int providers)
        => MetadataMerger.ApplyMatch(song, winner, confidence, providers, AutoUpgrade, warningsJson: null);

    private static EnrichmentProviderResult Winner(
        string? artist, string? title, int? year = null,
        string? spotifyId = null, string? isrc = null, string? mbid = null)
        => new(artist, artist, title, year, null, mbid, null, spotifyId, null, isrc,
            "TestProvider", 0.9, [], EnrichmentStatus.Matched);

    private static SongMetadata Song(string? artist, string? title) => new()
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
        EnrichmentStatus = EnrichmentStatus.Pending,
    };
}
