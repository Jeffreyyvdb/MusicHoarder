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

    [Fact]
    public void NewFields_AreApplied_OnEmptyExisting()
    {
        var song = Song(artist: null, title: "T");
        Merge(
            song,
            Winner(artist: "Alice feat. Bob", title: "T", artists: "Alice; Bob",
                releaseGroupId: "rg-1", albumArtistMbid: "aa-1", releaseTypePrimary: "single",
                releaseTypes: "single", discNumber: 1, totalDiscs: 2, totalTracks: 9),
            confidence: 0.92, providers: 1);

        Assert.Equal("Alice; Bob", song.Artists);
        Assert.Equal("rg-1", song.MusicBrainzReleaseGroupId);
        Assert.Equal("aa-1", song.AlbumArtistMusicBrainzId);
        Assert.Equal("single", song.ReleaseTypePrimary);
        Assert.Equal(1, song.DiscNumber);
        Assert.Equal(2, song.TotalDiscs);
        Assert.Equal(9, song.TotalTracks);
    }

    [Fact]
    public void Compilation_FlagIsAdditive()
    {
        var song = Song(artist: "A", title: "T");
        Assert.False(song.IsCompilation);

        Merge(song, Winner(artist: "A", title: "T", isCompilation: true), confidence: 0.90, providers: 1);

        Assert.True(song.IsCompilation);
    }

    [Fact]
    public void AlbumArtist_NeverBecomesFeaturedCredit()
    {
        // The provider sends a clean primary as AlbumArtist even though the track artist has a feat.
        var song = Song(artist: null, title: "T");
        Merge(
            song,
            Winner(artist: "Alice feat. Bob", title: "T", albumArtist: "Alice", artists: "Alice; Bob"),
            confidence: 0.92, providers: 1);

        Assert.Equal("Alice", song.AlbumArtist);
        Assert.DoesNotContain("feat", song.AlbumArtist!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AlbumCorroborated_UpgradesGoodEmbedded_EvenBelowBlanketConsensus()
    {
        // The cluster corroborated the album field (≥2 providers), so it may replace a good embedded
        // album even though the blanket high-consensus gate (providers≥2 && conf≥0.96) isn't met.
        var song = Song(artist: "Fugees", title: "Ready or Not");
        song.Album = "Greatest Hits";
        var changes = MetadataMerger.ApplyMatch(
            song, Winner(artist: "Fugees", title: "Ready or Not", album: "The Score"),
            confidence: 0.90, agreeingProviderCount: 2, AutoUpgrade, warningsJson: null,
            corroboratedFields: Fields("Album"));

        Assert.Equal("The Score", song.Album);
        Assert.Contains(changes, c => c is { Field: "Album", Applied: true });
    }

    [Fact]
    public void AlbumNotCorroborated_KeepsGoodEmbedded_ProposesChange()
    {
        // Album was NOT corroborated by the cluster → a good embedded album must be preserved and the
        // provider's value only proposed, regardless of how confident the recording match was.
        var song = Song(artist: "Fugees", title: "Ready or Not");
        song.Album = "The Score (Expanded Edition)";
        var changes = MetadataMerger.ApplyMatch(
            song, Winner(artist: "Fugees", title: "Ready or Not", album: "Greatest Hits"),
            confidence: 0.99, agreeingProviderCount: 3, AutoUpgrade, warningsJson: null,
            corroboratedFields: Fields()); // empty: recording corroborated, album not

        Assert.Equal("The Score (Expanded Edition)", song.Album);
        Assert.Contains(changes, c => c is { Field: "Album", Applied: false });
    }

    [Fact]
    public void YearNotCorroborated_KeepsGoodEmbedded_EvenAtHighRecordingConsensus()
    {
        // Song 1374: the recording is corroborated by 3 providers at high confidence, but the year is
        // not (providers split 2000/2003/1996). The embedded 1996 must survive.
        var song = Song(artist: "Fugees", title: "No Woman, No Cry");
        song.Year = 1996;
        var changes = MetadataMerger.ApplyMatch(
            song, Winner(artist: "Fugees", title: "No Woman, No Cry", year: 2000),
            confidence: 0.99, agreeingProviderCount: 3, AutoUpgrade, warningsJson: null,
            corroboratedFields: Fields("Album")); // album corroborated, year not

        Assert.Equal(1996, song.Year);
        Assert.Contains(changes, c => c is { Field: "Year", Applied: false });
    }

    // --- helpers ---

    private static IReadOnlySet<string> Fields(params string[] fields) => new HashSet<string>(fields);

    private static IReadOnlyList<MetadataMerger.FieldChange> Merge(
        SongMetadata song, EnrichmentProviderResult winner, double confidence, int providers)
        => MetadataMerger.ApplyMatch(song, winner, confidence, providers, AutoUpgrade, warningsJson: null);

    private static EnrichmentProviderResult Winner(
        string? artist, string? title, int? year = null,
        string? spotifyId = null, string? isrc = null, string? mbid = null,
        string? albumArtist = null, string? artists = null, string? releaseGroupId = null,
        string? albumArtistMbid = null, string? releaseTypePrimary = null, string? releaseTypes = null,
        int? discNumber = null, int? totalDiscs = null, int? totalTracks = null, bool? isCompilation = null,
        string? album = null)
        => new(artist, albumArtist ?? artist, title, year, null, mbid, null, spotifyId, null, isrc,
            "TestProvider", 0.9, [], EnrichmentStatus.Matched,
            Album: album, Artists: artists, ArtistMusicBrainzIds: null,
            AlbumArtistMusicBrainzId: albumArtistMbid, MusicBrainzReleaseGroupId: releaseGroupId,
            DiscNumber: discNumber, TotalDiscs: totalDiscs, TotalTracks: totalTracks,
            IsCompilation: isCompilation, ReleaseTypePrimary: releaseTypePrimary, ReleaseTypes: releaseTypes);

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
