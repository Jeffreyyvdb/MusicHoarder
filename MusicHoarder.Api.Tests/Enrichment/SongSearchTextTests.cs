using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

public class SongSearchTextTests
{
    [Fact]
    public void EmbeddedTags_WinOverPath()
    {
        var song = Song("/root/music/Path Artist/Album/01 Path Title.mp3", artist: "Tag Artist", title: "Tag Title");

        var (artist, title) = SongSearchText.Resolve(song, "/root/music");

        Assert.Equal("Tag Artist", artist);
        Assert.Equal("Tag Title", title);
    }

    [Fact]
    public void Untagged_ArtistAlbumTrackLayout_DerivesTopLevelArtistAndFileTitle()
    {
        var song = Song("/root/music/Juice WRLD/Goodbye & Good Riddance/05 Lucid Dreams.mp3");

        var (artist, title) = SongSearchText.Resolve(song, "/root/music");

        Assert.Equal("Juice WRLD", artist);
        Assert.Equal("Lucid Dreams", title);
    }

    [Fact]
    public void Untagged_ArtistTrackLayout_DerivesArtistDirectlyUnderRoot()
    {
        var song = Song("/music/Adele/Hello.mp3");

        var (artist, title) = SongSearchText.Resolve(song, "/music");

        Assert.Equal("Adele", artist);
        Assert.Equal("Hello", title);
    }

    [Fact]
    public void Untagged_FileDirectlyUnderRoot_HasNoArtist()
    {
        var song = Song("/music/loose-track.mp3");

        var (artist, title) = SongSearchText.Resolve(song, "/music");

        Assert.Null(artist);
        Assert.Equal("loose-track", title);
    }

    [Theory]
    [InlineData("/m/Artist/Album/01 - Song Name.mp3", "Song Name")]
    [InlineData("/m/Artist/Album/01. Song Name.flac", "Song Name")]
    [InlineData("/m/Artist/Album/1-01 Song Name.mp3", "Song Name")]
    [InlineData("/m/Artist/Album/04 Field Trip.mp3", "Field Trip")]
    [InlineData("/m/Artist/Album/Song_Name.opus", "Song Name")]
    public void Untagged_StripsLeadingTrackNumberPrefixes(string path, string expectedTitle)
    {
        var song = Song(path);

        var (_, title) = SongSearchText.Resolve(song, "/m");

        Assert.Equal(expectedTitle, title);
    }

    [Fact]
    public void Untagged_ArtistTitleFilename_StripsArtistAbbreviationPrefix()
    {
        // "Juice" is an abbreviation of the "Juice WRLD" folder — the title is "Benjamin".
        var song = Song("/root/music/Juice WRLD/Loose downloads discord/Juice - Benjamin.mp3");

        var (artist, title) = SongSearchText.Resolve(song, "/root/music");

        Assert.Equal("Juice WRLD", artist);
        Assert.Equal("Benjamin", title);
    }

    [Fact]
    public void Untagged_ArtistTitleFilename_StripsFullArtistPrefix()
    {
        var song = Song("/root/music/Juice WRLD/Leaks/Juice WRLD - Benjamin.mp3");

        var (_, title) = SongSearchText.Resolve(song, "/root/music");

        Assert.Equal("Benjamin", title);
    }

    [Theory]
    [InlineData("Juice – Benjamin")] // en-dash
    [InlineData("Juice — Benjamin")] // em-dash
    [InlineData("Juice ― Benjamin")] // horizontal bar
    [InlineData("Juice − Benjamin")] // minus sign
    public void Untagged_ArtistTitleFilename_StripsPrefixRegardlessOfDashType(string stem)
    {
        // Discord / loose-download filenames use a variety of Unicode dashes, not just "-".
        var song = Song($"/root/music/Juice WRLD/Loose downloads discord/{stem}.mp3");

        var (artist, title) = SongSearchText.Resolve(song, "/root/music");

        Assert.Equal("Juice WRLD", artist);
        Assert.Equal("Benjamin", title);
    }

    [Fact]
    public void Untagged_HyphenatedTitleWord_NotMatchingArtist_IsLeftIntact()
    {
        // "Anti-Hero" has no surrounding whitespace, so it isn't a separator; lead also isn't artist.
        var song = Song("/root/music/Taylor Swift/Midnights/Anti-Hero.mp3");

        var (_, title) = SongSearchText.Resolve(song, "/root/music");

        Assert.Equal("Anti-Hero", title);
    }

    [Fact]
    public void Untagged_TitleWithSeparator_NotMatchingArtist_IsLeftIntact()
    {
        // Lead segment "Robbery" is not the artist, so the " - " is part of the real title.
        var song = Song("/root/music/Some Artist/Album/Robbery - Live.mp3");

        var (_, title) = SongSearchText.Resolve(song, "/root/music");

        Assert.Equal("Robbery - Live", title);
    }

    [Fact]
    public void Untagged_FileUnderRootWithNoArtist_LeavesSeparatorTitleIntact()
    {
        // No artist folder to validate the lead against → don't split blindly.
        var song = Song("/music/Foo - Bar.mp3");

        var (artist, title) = SongSearchText.Resolve(song, "/music");

        Assert.Null(artist);
        Assert.Equal("Foo - Bar", title);
    }

    [Fact]
    public void Untagged_LooseDownloadFolder_ArtistTitleFilename_UsesFilenameAsArtist()
    {
        // "slskd" is a download-tool folder, not the performer — the artist lives in the filename.
        var song = Song("/music/source/slskd/Mac Miller - Someone Like You.mp3");

        var (artist, title) = SongSearchText.Resolve(song, "/music/source");

        Assert.Equal("Mac Miller", artist);
        Assert.Equal("Someone Like You", title);
    }

    [Fact]
    public void Untagged_LooseDownloadFolder_KeepsParentheticalSuffixInTitle()
    {
        // The "(WMWTSO)" suffix stays on the title; the normalizer drops it later for search/scoring.
        var song = Song("/music/source/slskd/Mac Miller - Avian (WMWTSO).mp3");

        var (artist, title) = SongSearchText.Resolve(song, "/music/source");

        Assert.Equal("Mac Miller", artist);
        Assert.Equal("Avian (WMWTSO)", title);
    }

    [Fact]
    public void Untagged_ShallowFolderEqualsArtist_FilenameSplitStillCorrect()
    {
        // Redundant "Artist - Title" filename under an artist folder still resolves correctly.
        var song = Song("/music/Adele/Adele - Hello.mp3");

        var (artist, title) = SongSearchText.Resolve(song, "/music");

        Assert.Equal("Adele", artist);
        Assert.Equal("Hello", title);
    }

    [Fact]
    public void Untagged_ShallowTrackNumberedFile_IsNotSplitOnDash()
    {
        // A track-number prefix marks the tagged "NN Title" convention — don't reinterpret the dash
        // as an artist separator; fall back to folder-as-artist.
        var song = Song("/music/slskd/01 - Mac Miller - Avian.mp3");

        var (artist, title) = SongSearchText.Resolve(song, "/music");

        Assert.Equal("slskd", artist);
        Assert.Equal("Mac Miller - Avian", title);
    }

    [Fact]
    public void HasSearchableText_TrueForUntaggedFileWithUsablePath()
    {
        var song = Song("/root/music/Some Artist/Album/Track.mp3");

        Assert.True(SongSearchText.HasSearchableText(song, "/root/music"));
    }

    [Fact]
    public void ResolveDetailed_Untagged_DerivesAlbumFromContainingDirectory()
    {
        var song = Song("/root/music/Juice WRLD/Goodbye & Good Riddance/05 Lucid Dreams.mp3");

        var resolved = SongSearchText.ResolveDetailed(song, "/root/music");

        Assert.Equal("Juice WRLD", resolved.Artist);
        Assert.Equal("Goodbye & Good Riddance", resolved.Album);
        Assert.Equal("Lucid Dreams", resolved.Title);
        Assert.Equal(5, resolved.TrackNumber);
    }

    [Fact]
    public void ResolveDetailed_ArtistTrackLayout_HasNoAlbum()
    {
        var song = Song("/music/Adele/Hello.mp3");

        var resolved = SongSearchText.ResolveDetailed(song, "/music");

        Assert.Equal("Adele", resolved.Artist);
        Assert.Null(resolved.Album);
        Assert.Equal("Hello", resolved.Title);
    }

    [Fact]
    public void ResolveDetailed_EmbeddedTags_WinOverPath()
    {
        var song = Song("/root/music/Path Artist/Path Album/01 Path Title.mp3",
            artist: "Tag Artist", title: "Tag Title", album: "Tag Album", trackNumber: 7);

        var resolved = SongSearchText.ResolveDetailed(song, "/root/music");

        Assert.Equal("Tag Artist", resolved.Artist);
        Assert.Equal("Tag Album", resolved.Album);
        Assert.Equal("Tag Title", resolved.Title);
        Assert.Equal(7, resolved.TrackNumber);
    }

    [Fact]
    public void ResolveDetailed_TrackNumberTag_WinsOverPathPrefix()
    {
        // Embedded track tag wins; path prefix ("05") is only a fallback.
        var song = Song("/m/A/Album/05 T.mp3", artist: "A", title: "T", trackNumber: 9);

        var resolved = SongSearchText.ResolveDetailed(song, "/m");

        Assert.Equal(9, resolved.TrackNumber);
    }

    [Theory]
    [InlineData("Various Artists")]
    [InlineData("various")]
    [InlineData("VA")]
    public void ResolveDetailed_CompilationFolder_DropsBogusArtistButKeepsAlbum(string folder)
    {
        var song = Song($"/music/{folder}/Now That's Music 50/03 Some Hit.mp3");

        var resolved = SongSearchText.ResolveDetailed(song, "/music");

        Assert.Null(resolved.Artist);
        Assert.Equal("Now That's Music 50", resolved.Album);
        Assert.Equal("Some Hit", resolved.Title);
    }

    [Fact]
    public void Untagged_SlskdBucketFolder_FlagsPathProvenanceAndCleansRawSearchText()
    {
        // A loose download two folders deep under an alphabetical bucket: the positional artist is the
        // junk "slskd" folder, but the filename free-text still carries the real artist + title.
        var song = Song("/root/music/slskd/A/Amy Macdonald - This Is the Life.flac");

        var resolved = SongSearchText.ResolveDetailed(song, "/root/music");

        Assert.True(resolved.IdentityFromPath);
        Assert.True(resolved.ArtistFromPath);
        Assert.Equal("Amy Macdonald This Is the Life", resolved.RawSearchText);
    }

    [Fact]
    public void Untagged_MessyFilename_StripsTrackNumberUnderscoresAndProdCredit()
    {
        // Track-number prefix with no trailing space, underscores-as-spaces, two dashes, and a
        // "(prod by …)" production credit — a positional split can't parse this, but the free-text can.
        var song = Song("/root/music/slskd/H/20-luie_mannen-hef_(prod_by_dj_mp).mp3");

        var resolved = SongSearchText.ResolveDetailed(song, "/root/music");

        Assert.Equal("luie mannen hef", resolved.RawSearchText);
    }

    [Fact]
    public void TaggedArtistTitle_IsNotPathDerived()
    {
        var song = Song("/root/music/slskd/A/Amy Macdonald - This Is the Life.flac",
            artist: "Amy Macdonald", title: "This Is the Life");

        var resolved = SongSearchText.ResolveDetailed(song, "/root/music");

        Assert.False(resolved.IdentityFromPath);
        Assert.False(resolved.ArtistFromPath);
        Assert.False(resolved.TitleFromPath);
    }

    [Fact]
    public void PathQuery_DropsBucketFolder_WhenFilenameCarriesArtist()
    {
        var song = Song("/root/music/slskd/A/Amy Macdonald - This Is the Life.flac");

        var resolved = SongSearchText.ResolveDetailed(song, "/root/music");

        // The filename carries the artist ("Artist - Title"), so the junk "slskd"/"A" bucket folders are
        // kept OUT of the query — prepending them measurably degrades a real search engine's ranking.
        Assert.True(resolved.FilenameCarriesArtist);
        Assert.Equal("Amy Macdonald This Is the Life", resolved.PathQuery);
    }

    [Fact]
    public void PathQuery_PrependsFolderArtist_ForStructuredUntaggedFile()
    {
        // Structured "<Artist>/<Album>/NN Title" with no tags: the artist lives in the folder, not the
        // bare-title filename, so it IS prepended so the search engine keeps the artist.
        var song = Song("/root/music/Juice WRLD/Goodbye & Good Riddance/05 Lucid Dreams.mp3");

        var resolved = SongSearchText.ResolveDetailed(song, "/root/music");

        Assert.False(resolved.FilenameCarriesArtist);
        Assert.Equal("Juice WRLD Lucid Dreams", resolved.PathQuery);
    }

    private static SongMetadata Song(
        string sourcePath, string? artist = null, string? title = null,
        string? album = null, int? trackNumber = null) => new()
    {
        OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
        SourcePath = sourcePath,
        FileName = sourcePath.Split('/')[^1],
        Extension = ".mp3",
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Artist = artist,
        Title = title,
        Album = album,
        TrackNumber = trackNumber,
        EnrichmentStatus = EnrichmentStatus.Pending,
    };
}
