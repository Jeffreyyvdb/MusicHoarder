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
        // A missing title falls back to the source file name's stem — NOT a shared "Unknown Title"
        // (which would collide every titleless track of an album onto one destination path).
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
            Path.Combine(DestinationRoot, "Unknown Artist", "Test Album", "song.mp3"),
            path);
    }

    [Fact]
    public void ResolvePath_TitlelessTracksWithDistinctFileNames_ResolveToDistinctPaths()
    {
        // Regression: untagged tracker files (no title, no track number) built via
        // EnableBuildNeedsReview must not all collapse onto ".../Unknown Title.mp3" — that shared
        // path made each build silently overwrite the previous track's file and pushed the
        // meaningless "Unknown Title" name through instance sync.
        var resolver = CreateResolver();
        var first = CreateSong(
            artist: "Juice WRLD", albumArtist: "Juice WRLD", album: "Affliction (sessions)",
            title: null, year: 2016, trackNumber: null);
        first.FileName = "2MININHELL Pt. 2 (Lose Me).mp3";
        var second = CreateSong(
            artist: "Juice WRLD", albumArtist: "Juice WRLD", album: "Affliction (sessions)",
            title: null, year: 2016, trackNumber: null);
        second.FileName = "Fuck It Up (feat. King Jefe).mp3";

        var firstPath = resolver.ResolvePath(first);
        var secondPath = resolver.ResolvePath(second);

        Assert.Equal(
            Path.Combine(DestinationRoot, "Juice WRLD", "2016 - Affliction (sessions)", "2MININHELL Pt. 2 (Lose Me).mp3"),
            firstPath);
        Assert.NotEqual(firstPath, secondPath);
    }

    [Fact]
    public void ResolvePath_TitlelessTrackWithUnusableFileName_FallsBackToUnknownTitle()
    {
        var resolver = CreateResolver();
        var song = CreateSong(
            artist: "Artist", albumArtist: null, album: "Album",
            title: "   ", year: null, trackNumber: null);
        song.FileName = "???.mp3"; // sanitizes to an empty stem

        var path = resolver.ResolvePath(song);

        Assert.Equal(
            Path.Combine(DestinationRoot, "Artist", "Album", "Unknown Title.mp3"),
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
    public void ResolvePath_WithVariousArtistsCompilation_RoutesToVariousArtistsTree()
    {
        // A genuine various-artists compilation (album artist literally "Various Artists") routes
        // under the Various Artists tree so the album stays together.
        var resolver = CreateResolver();
        var song = CreateSong(
            artist: "Various Performers",
            albumArtist: "Various Artists",
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

    [Theory]
    [InlineData("Verschiedene Interpreten")]
    [InlineData("Varios Artistas")]
    [InlineData("Artisti Vari")]
    [InlineData("Diverse Artiesten")]
    public void ResolvePath_WithLocalizedVariousArtistsName_RoutesToVariousArtistsTree(string albumArtist)
    {
        // A provider answers in whatever locale it feels like — Spotify returned "Verschiedene
        // Interpreten" for a Top Boy compilation, which earned those tracks a top-level artist
        // folder of their own right next to Various Artists.
        var resolver = CreateResolver();
        var song = CreateSong(
            artist: "Dave",
            albumArtist: albumArtist,
            album: "Top Boy",
            title: "Professor X",
            year: 2019,
            trackNumber: 7,
            isCompilation: true);

        var path = resolver.ResolvePath(song);

        Assert.Equal(
            Path.Combine(DestinationRoot, "Various Artists", "2019 - Top Boy", "07 - Professor X.mp3"),
            path);
    }

    [Fact]
    public void ResolvePath_SingleArtistTrackOnCompilationFlaggedRelease_FilesUnderArtist()
    {
        // RHCP "Scar Tissue" matched to a compilation ("Greatest Hits and Videos") that a provider
        // flagged IsCompilation. The track is still by a single artist, so it must file under that
        // artist — NOT get exiled to a Various Artists folder.
        var resolver = CreateResolver();
        var song = CreateSong(
            artist: "Red Hot Chili Peppers",
            albumArtist: "Red Hot Chili Peppers",
            album: "Greatest Hits and Videos",
            title: "Scar Tissue",
            year: 2003,
            trackNumber: 3,
            isCompilation: true);

        var path = resolver.ResolvePath(song);

        Assert.Equal(
            Path.Combine(DestinationRoot, "Red Hot Chili Peppers", "2003 - Greatest Hits and Videos", "03 - Scar Tissue.mp3"),
            path);
    }

    [Fact]
    public void ResolvePath_CompilationWithoutAlbumArtist_RoutesToVariousArtistsTree()
    {
        // No album artist + compilation flag is the classic various-artists case → Various Artists.
        var resolver = CreateResolver();
        var song = CreateSong(
            artist: "Various Performers",
            albumArtist: null,
            album: "Summer Hits",
            title: "A Hit",
            year: 2005,
            trackNumber: 4,
            isCompilation: true);

        var path = resolver.ResolvePath(song);

        Assert.Equal(
            Path.Combine(DestinationRoot, "Various Artists", "2005 - Summer Hits", "04 - A Hit.mp3"),
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

    [Fact]
    public void ResolveLegacyPath_UsesRawTrackArtist_NotAlbumArtist()
    {
        // The legacy scheme routed by the unsplit track artist; the current scheme elects the
        // album artist. Both are pinned so the divergence stays visible.
        var resolver = CreateResolver();
        var song = CreateSong(
            artist: "Artist A; Artist B",
            albumArtist: "Artist A",
            album: "Album",
            title: "Track",
            year: 2026,
            trackNumber: 1);

        Assert.Equal(
            Path.Combine(DestinationRoot, "Artist A; Artist B", "2026 - Album", "01 - Track.mp3"),
            resolver.ResolveLegacyPath(song));
        Assert.Equal(
            Path.Combine(DestinationRoot, "Artist A", "2026 - Album", "01 - Track.mp3"),
            resolver.ResolvePath(song));
    }

    [Fact]
    public void ResolveLegacyPath_UsesUnknownTitleFallback_NotFileNameStem()
    {
        // Historic builds wrote untitled tracks as "Unknown Title"; the current scheme falls back
        // to the file name's stem. The legacy resolver must keep producing the historic name.
        var resolver = CreateResolver();
        var song = CreateSong(
            artist: "Artist",
            albumArtist: "Artist",
            album: "Album",
            title: null,
            year: 2026,
            trackNumber: 1);

        Assert.Equal(
            Path.Combine(DestinationRoot, "Artist", "2026 - Album", "01 - Unknown Title.mp3"),
            resolver.ResolveLegacyPath(song));
        Assert.Equal(
            Path.Combine(DestinationRoot, "Artist", "2026 - Album", "01 - song.mp3"),
            resolver.ResolvePath(song));
    }

    [Fact]
    public void ResolveLegacyPath_WithUnreleasedTrack_UsesUnreleasedFolder()
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

        Assert.Equal(
            Path.Combine(DestinationRoot, "Juice WRLD", "Unreleased", "Righteous (CDQ).mp3"),
            resolver.ResolveLegacyPath(song));
    }

    [Fact]
    public void ResolveLegacyPath_IgnoresCompilationRouting_AndDiscPrefix()
    {
        // The legacy scheme predates Various-Artists routing and multi-disc prefixes: a
        // compilation track still files under its raw artist, and disc 2's track 1 is a
        // plain "01 - " like any other.
        var resolver = CreateResolver();
        var song = CreateSong(
            artist: "Artist",
            albumArtist: "Various Artists",
            album: "Compilation",
            title: "Track",
            year: 2020,
            trackNumber: 1,
            isCompilation: true,
            discNumber: 2,
            totalDiscs: 2);

        Assert.Equal(
            Path.Combine(DestinationRoot, "Artist", "2020 - Compilation", "01 - Track.mp3"),
            resolver.ResolveLegacyPath(song));
    }

    [Fact]
    public void ResolveLegacyPath_SanitizesAndTruncatesSegments()
    {
        var resolver = CreateResolver();
        var song = CreateSong(
            artist: "AC/DC",
            albumArtist: null,
            album: new string('a', 80),
            title: "Back In Black?",
            year: null,
            trackNumber: 6);

        Assert.Equal(
            Path.Combine(DestinationRoot, "ACDC", new string('a', 60), "06 - Back In Black.mp3"),
            resolver.ResolveLegacyPath(song));
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
