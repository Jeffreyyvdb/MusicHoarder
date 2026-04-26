using System.Reflection;
using System.Text.Json;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Endpoints;

public class EnrichmentDetailHelperTests
{
    [Fact]
    public void BuildMetadataDiff_AllFieldsUnchanged_ReturnsEmpty()
    {
        var song = new SongMetadata
        {
            SourcePath = "/a.mp3",
            FileName = "a.mp3",
            Extension = ".mp3",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Title = "T", Artist = "A", AlbumArtist = "A", Album = "Al",
            Year = 2020, TrackNumber = 3, Isrc = "USRC1",
            MusicBrainzId = "mb-1", SpotifyId = "sp-1",
            OriginalMetadataCaptured = true,
            OriginalTitle = "T", OriginalArtist = "A", OriginalAlbumArtist = "A", OriginalAlbum = "Al",
            OriginalYear = 2020, OriginalTrackNumber = 3, OriginalIsrc = "USRC1",
            OriginalMusicBrainzId = "mb-1", OriginalSpotifyId = "sp-1",
        };

        var diff = SongsEndpoints.BuildMetadataDiff(song);

        Assert.Empty(diff);
    }

    [Fact]
    public void BuildMetadataDiff_ChangedStringField_IsListed()
    {
        var song = new SongMetadata
        {
            SourcePath = "/a.mp3", FileName = "a.mp3", Extension = ".mp3",
            FileSizeBytes = 1, LastModifiedUtc = DateTime.UtcNow, IndexedAtUtc = DateTime.UtcNow,
            Title = "Foo", OriginalTitle = "Foo (Remastered 2009)",
            OriginalMetadataCaptured = true,
        };

        var diff = SongsEndpoints.BuildMetadataDiff(song);

        var entry = Assert.Single(diff);
        AssertDiffEntry(entry, field: "title",
            expectedSource: "Foo (Remastered 2009)", expectedCurrent: "Foo");
    }

    [Fact]
    public void BuildMetadataDiff_NullVsEmpty_TreatedAsEqual()
    {
        var song = new SongMetadata
        {
            SourcePath = "/a.mp3", FileName = "a.mp3", Extension = ".mp3",
            FileSizeBytes = 1, LastModifiedUtc = DateTime.UtcNow, IndexedAtUtc = DateTime.UtcNow,
            Album = "", OriginalAlbum = null,
            Title = "  ", OriginalTitle = "",
            OriginalMetadataCaptured = true,
        };

        var diff = SongsEndpoints.BuildMetadataDiff(song);

        Assert.Empty(diff);
    }

    [Fact]
    public void BuildMetadataDiff_ChangedIntField_IsListed()
    {
        var song = new SongMetadata
        {
            SourcePath = "/a.mp3", FileName = "a.mp3", Extension = ".mp3",
            FileSizeBytes = 1, LastModifiedUtc = DateTime.UtcNow, IndexedAtUtc = DateTime.UtcNow,
            Year = 2020, OriginalYear = 2018,
            OriginalMetadataCaptured = true,
        };

        var diff = SongsEndpoints.BuildMetadataDiff(song);

        var entry = Assert.Single(diff);
        AssertDiffEntry(entry, field: "year", expectedSource: 2018, expectedCurrent: 2020);
    }

    [Fact]
    public void BuildMetadataDiff_AlbumDropped_IsListedWithNullCurrent()
    {
        var song = new SongMetadata
        {
            SourcePath = "/a.mp3", FileName = "a.mp3", Extension = ".mp3",
            FileSizeBytes = 1, LastModifiedUtc = DateTime.UtcNow, IndexedAtUtc = DateTime.UtcNow,
            Album = null, OriginalAlbum = "Greatest Hits",
            OriginalMetadataCaptured = true,
        };

        var diff = SongsEndpoints.BuildMetadataDiff(song);

        var entry = Assert.Single(diff);
        AssertDiffEntry(entry, field: "album",
            expectedSource: "Greatest Hits", expectedCurrent: null);
    }

    [Fact]
    public void DeserializeCandidate_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(SongsEndpoints.DeserializeCandidate(null));
        Assert.Null(SongsEndpoints.DeserializeCandidate(""));
        Assert.Null(SongsEndpoints.DeserializeCandidate("   "));
    }

    [Fact]
    public void DeserializeCandidate_GarbageJson_ReturnsNull()
    {
        Assert.Null(SongsEndpoints.DeserializeCandidate("not-json-at-all"));
    }

    [Fact]
    public void DeserializeCandidate_ValidJson_ReturnsProjectedObjectWithStatusAsString()
    {
        var original = new EnrichmentProviderResult(
            Artist: "A", AlbumArtist: "A", Title: "T",
            Year: 2020, TrackNumber: 5,
            MusicBrainzId: "mb", MusicBrainzReleaseId: "mbr",
            SpotifyId: "sp", AcoustIdTrackId: "aid",
            Isrc: "USRC9",
            MatchedBy: "SpotifyAPI",
            MatchConfidence: 0.42,
            MatchWarnings: ["duration_mismatch"],
            RecommendedStatus: EnrichmentStatus.NeedsReview,
            Album: "Al");
        var json = JsonSerializer.Serialize(original);

        var projected = SongsEndpoints.DeserializeCandidate(json);

        Assert.NotNull(projected);
        Assert.Equal("T", GetProperty<string>(projected!, "title"));
        Assert.Equal("A", GetProperty<string>(projected!, "artist"));
        Assert.Equal("Al", GetProperty<string>(projected!, "album"));
        Assert.Equal(2020, GetProperty<int?>(projected!, "year"));
        Assert.Equal("SpotifyAPI", GetProperty<string>(projected!, "matchedBy"));
        Assert.Equal(0.42, GetProperty<double>(projected!, "matchConfidence"));
        Assert.Equal("NeedsReview", GetProperty<string>(projected!, "recommendedStatus"));
    }

    private static void AssertDiffEntry(object entry, string field, object? expectedSource, object? expectedCurrent)
    {
        Assert.Equal(field, GetProperty<string>(entry, "field"));
        Assert.Equal(expectedSource, GetProperty<object?>(entry, "source"));
        Assert.Equal(expectedCurrent, GetProperty<object?>(entry, "current"));
    }

    private static T GetProperty<T>(object obj, string name)
    {
        var prop = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Property '{name}' not found on {obj.GetType()}");
        var value = prop.GetValue(obj);
        return (T)value!;
    }
}
