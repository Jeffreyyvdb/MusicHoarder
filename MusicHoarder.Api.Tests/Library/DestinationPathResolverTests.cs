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
            albumArtist: null,
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
            albumArtist: null,
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
            albumArtist: null,
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
            albumArtist: null,
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
            albumArtist: null,
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
            albumArtist: null,
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
            albumArtist: null,
            album: null,
            title: "Track",
            year: null,
            trackNumber: null);

        var path = resolver.ResolvePath(song);

        Assert.Equal(
            Path.Combine(DestinationRoot, "Artist", "Unknown Album", "Track.mp3"),
            path);
    }

    [Fact]
    public void ResolvePath_UsesAlbumArtistFolder_WhenAvailable()
    {
        var resolver = CreateResolver();
        var song = CreateSong(
            artist: "The Notorious B.I.G.; The Lox",
            albumArtist: "The Notorious B.I.G.",
            album: "Life After Death",
            title: "Last Day",
            year: 1997,
            trackNumber: 19);

        var path = resolver.ResolvePath(song);

        Assert.Equal(
            Path.Combine(DestinationRoot, "The Notorious B.I.G.", "1997 - Life After Death", "19 - Last Day.mp3"),
            path);
    }

    [Fact]
    public void ResolvePath_WithoutAlbumArtist_UsesPrimaryArtistFromCredit()
    {
        var resolver = CreateResolver();
        var song = CreateSong(
            artist: "Drake; Rihanna",
            albumArtist: null,
            album: "Take Care",
            title: "Take Care",
            year: 2011,
            trackNumber: 12);

        var path = resolver.ResolvePath(song);

        Assert.Equal(
            Path.Combine(DestinationRoot, "Drake", "2011 - Take Care", "12 - Take Care.mp3"),
            path);
    }

    [Fact]
    public void ResolvePath_WithCompilation_RoutesToVariousArtistsTree()
    {
        var resolver = CreateResolver();
        var song = CreateSong(
            artist: "Various Performers",
            albumArtist: "Some Label",
            album: "Now That's What I Call Music",
            title: "A Hit",
            year: 2001,
            trackNumber: 7,
            isCompilation: true);

        var path = resolver.ResolvePath(song);

        Assert.Equal(
            Path.Combine(DestinationRoot, "Various Artists", "2001 - Now That's What I Call Music", "07 - A Hit.mp3"),
            path);
    }

    [Fact]
    public void ResolvePath_WithCustomCompilationFolder_UsesConfiguredName()
    {
        var resolver = CreateResolver(compilationFolderName: "Compilations");
        var song = CreateSong(
            artist: "Artist",
            albumArtist: null,
            album: "Mixtape",
            title: "Track",
            year: 2010,
            trackNumber: 3,
            isCompilation: true);

        var path = resolver.ResolvePath(song);

        Assert.Equal(
            Path.Combine(DestinationRoot, "Compilations", "2010 - Mixtape", "03 - Track.mp3"),
            path);
    }

    [Fact]
    public void ResolvePath_WithMultiDisc_PrefixesDiscNumber()
    {
        var resolver = CreateResolver();
        var song = CreateSong(
            artist: "Outkast",
            albumArtist: "Outkast",
            album: "Speakerboxxx / The Love Below",
            title: "Roses",
            year: 2003,
            trackNumber: 5,
            discNumber: 2,
            totalDiscs: 2);

        var path = resolver.ResolvePath(song);

        Assert.Equal(
            Path.Combine(DestinationRoot, "Outkast", "2003 - Speakerboxxx  The Love Below", "2-05 - Roses.mp3"),
            path);
    }

    [Fact]
    public void ResolvePath_WithSingleDisc_OmitsDiscPrefix()
    {
        var resolver = CreateResolver();
        var song = CreateSong(
            artist: "Artist",
            albumArtist: "Artist",
            album: "Album",
            title: "Track",
            year: 2020,
            trackNumber: 5,
            discNumber: 1,
            totalDiscs: 1);

        var path = resolver.ResolvePath(song);

        Assert.Equal(
            Path.Combine(DestinationRoot, "Artist", "2020 - Album", "05 - Track.mp3"),
            path);
    }

    private static DestinationPathResolver CreateResolver(string compilationFolderName = "Various Artists")
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/source",
            DestinationDirectory = DestinationRoot,
            CompilationFolderName = compilationFolderName,
        });

        return new DestinationPathResolver(options);
    }

    private static SongMetadata CreateSong(
        string? artist,
        string? albumArtist,
        string? album,
        string? title,
        int? year,
        int? trackNumber,
        bool isUnreleased = false,
        bool isCompilation = false,
        int? discNumber = null,
        int? totalDiscs = null)
    {
        return new SongMetadata
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            SourcePath = "/source/song.mp3",
            FileSizeBytes = 1000,
            FileName = "song.mp3",
            Extension = ".mp3",
            LastModifiedUtc = DateTime.UtcNow,
            Artist = artist,
            AlbumArtist = albumArtist,
            Album = album,
            Title = title,
            Year = year,
            TrackNumber = trackNumber,
            IndexedAtUtc = DateTime.UtcNow,
            IsUnreleased = isUnreleased,
            IsCompilation = isCompilation,
            DiscNumber = discNumber,
            TotalDiscs = totalDiscs
        };
    }
}
