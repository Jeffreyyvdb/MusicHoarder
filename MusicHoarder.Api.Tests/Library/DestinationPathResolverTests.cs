using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Library;

public class DestinationPathResolverTests
{
    private const string DestinationRoot = "/dest-root";

    [Fact]
    public void ResolvePath_WithFullMetadata_ReturnsExpectedPath()
    {
        var resolver = CreateResolver();
        var song = CreateSong(
            artist: "Kanye West",
            album: "The College Dropout",
            title: "Through The Wire",
            year: 2004,
            trackNumber: 1);

        var path = resolver.ResolvePath(song);

        Assert.Equal(
            Path.Combine(DestinationRoot, "Kanye West", "2004 - The College Dropout", "01 - Through The Wire.mp3"),
            path);
    }

    [Fact]
    public void ResolvePath_WithUnreleasedTrack_UsesUnreleasedFolder()
    {
        var resolver = CreateResolver();
        var song = CreateSong(
            artist: "Juice WRLD",
            album: null,
            title: "Righteous (CDQ)",
            year: null,
            trackNumber: null,
            isUnreleased: true);

        var path = resolver.ResolvePath(song);

        Assert.Equal(
            Path.Combine(DestinationRoot, "Juice WRLD", "Unreleased", "Righteous (CDQ).mp3"),
            path);
    }

    [Fact]
    public void ResolvePath_WithoutYear_OmitsYearPrefix()
    {
        var resolver = CreateResolver();
        var song = CreateSong(
            artist: "Juice WRLD",
            album: "Goodbye & Good Riddance",
            title: "Lucid Dreams",
            year: null,
            trackNumber: 1);

        var path = resolver.ResolvePath(song);

        Assert.Equal(
            Path.Combine(DestinationRoot, "Juice WRLD", "Goodbye & Good Riddance", "01 - Lucid Dreams.mp3"),
            path);
    }

    [Fact]
    public void ResolvePath_WithoutTrackNumber_OmitsTrackPrefix()
    {
        var resolver = CreateResolver();
        var song = CreateSong(
            artist: "Juice WRLD",
            album: "Goodbye & Good Riddance",
            title: "Lucid Dreams",
            year: 2018,
            trackNumber: null);

        var path = resolver.ResolvePath(song);

        Assert.Equal(
            Path.Combine(DestinationRoot, "Juice WRLD", "2018 - Goodbye & Good Riddance", "Lucid Dreams.mp3"),
            path);
    }

    [Fact]
    public void Sanitize_RemovesForbiddenFilesystemCharacters()
    {
        var input = "A\\B/C:D*E?F\"G<H>I|J";

        var sanitized = DestinationPathResolver.Sanitize(input);

        Assert.Equal("ABCDEFGHIJ", sanitized);
    }

    [Fact]
    public void ResolvePath_TruncatesLongSegmentsToSixtyCharacters()
    {
        var resolver = CreateResolver();
        var longArtist = new string('A', 75);
        var longAlbum = new string('B', 90);
        var longTitle = new string('C', 80);
        var song = CreateSong(
            artist: longArtist,
            album: longAlbum,
            title: longTitle,
            year: null,
            trackNumber: null);

        var path = resolver.ResolvePath(song);

        var relativePath = Path.GetRelativePath(DestinationRoot, path);
        var segments = relativePath.Split(Path.DirectorySeparatorChar);

        Assert.Equal(60, segments[0].Length);
        Assert.Equal(60, segments[1].Length);
        Assert.Equal(new string('A', 60), segments[0]);
        Assert.Equal(new string('B', 60), segments[1]);
        Assert.StartsWith(new string('C', 60), Path.GetFileNameWithoutExtension(segments[2]));
    }

    [Fact]
    public void ResolvePath_WithMissingArtistAndTitle_UsesFallbacks()
    {
        var resolver = CreateResolver();
        var song = CreateSong(
            artist: "  ",
            album: "Test Album",
            title: null,
            year: null,
            trackNumber: null);

        var path = resolver.ResolvePath(song);

        Assert.Equal(
            Path.Combine(DestinationRoot, "Unknown Artist", "Test Album", "Unknown Title.mp3"),
            path);
    }

    [Fact]
    public void ResolvePath_WithMissingAlbum_UsesUnknownAlbumFallback()
    {
        var resolver = CreateResolver();
        var song = CreateSong(
            artist: "Artist",
            album: null,
            title: "Track",
            year: null,
            trackNumber: null);

        var path = resolver.ResolvePath(song);

        Assert.Equal(
            Path.Combine(DestinationRoot, "Artist", "Unknown Album", "Track.mp3"),
            path);
    }

    private static DestinationPathResolver CreateResolver()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/source",
            DestinationDirectory = DestinationRoot,
            TempDirectory = "/tmp/musicenricher"
        });

        return new DestinationPathResolver(options);
    }

    private static SongMetadata CreateSong(
        string? artist,
        string? album,
        string? title,
        int? year,
        int? trackNumber,
        bool isUnreleased = false)
    {
        return new SongMetadata
        {
            SourcePath = "/source/song.mp3",
            FileSizeBytes = 1000,
            FileName = "song.mp3",
            Extension = ".mp3",
            LastModifiedUtc = DateTime.UtcNow,
            Artist = artist,
            Album = album,
            Title = title,
            Year = year,
            TrackNumber = trackNumber,
            IndexedAtUtc = DateTime.UtcNow,
            IsUnreleased = isUnreleased
        };
    }
}
