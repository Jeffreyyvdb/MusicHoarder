using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Library;

public class AlbumGroupKeyTests
{
    [Fact]
    public void For_YearDivergence_SameKey()
    {
        // Year is the most common split (the destination folder is "{Year} - {Album}"), so it must
        // not participate in the logical-album key.
        var a = Song(album: "Graduation", year: 2007);
        var b = Song(album: "Graduation", year: 2008);

        Assert.Equal(AlbumGroupKey.For(a), AlbumGroupKey.For(b));
    }

    [Fact]
    public void For_NormalizedTitleAndArtistVariants_SameKey()
    {
        var a = Song(album: "Believe", albumArtist: "Beyoncé");
        var b = Song(album: "Belíeve", albumArtist: "Beyonce");

        Assert.Equal(AlbumGroupKey.For(a), AlbumGroupKey.For(b));
    }

    [Theory]
    [InlineData("Graduation", "Graduation (Deluxe Edition)")]
    [InlineData("Graduation", "Graduation (Live)")]
    [InlineData("Graduation", "Graduation (Remastered)")]
    [InlineData("Graduation (Deluxe)", "Graduation (Live)")]
    public void For_EditionQualifiers_DistinctKeys(string albumA, string albumB)
    {
        // NormalizeForSearch strips parenthesized qualifiers, so without the VersionQualifier
        // discriminator a deluxe/live edition would merge into the standard album — the one
        // dangerous direction.
        var a = Song(album: albumA);
        var b = Song(album: albumB);

        Assert.NotEqual(AlbumGroupKey.For(a), AlbumGroupKey.For(b));
    }

    [Fact]
    public void For_SameQualifierDifferentSpelling_SameKey()
    {
        var a = Song(album: "Graduation (Deluxe)");
        var b = Song(album: "Graduation (Deluxe Edition)");

        Assert.Equal(AlbumGroupKey.For(a), AlbumGroupKey.For(b));
    }

    [Fact]
    public void For_VariousArtistsCompilations_GroupAcrossSentinelSpellings()
    {
        var a = Song(album: "Now That's Music", albumArtist: "Various Artists", isCompilation: true);
        var b = Song(album: "Now That's Music", albumArtist: "VA", isCompilation: true);
        var c = Song(album: "Now That's Music", albumArtist: null, isCompilation: true);

        Assert.Equal(AlbumGroupKey.For(a), AlbumGroupKey.For(b));
        Assert.Equal(AlbumGroupKey.For(a), AlbumGroupKey.For(c));
        Assert.Equal(AlbumGroupKey.VariousArtistsKey, AlbumGroupKey.For(a)!.ArtistKey);
    }

    [Fact]
    public void For_CompilationFlaggedSingleArtistRelease_StaysUnderArtist()
    {
        // A greatest-hits a provider flagged "compilation" but with a real album artist must not be
        // exiled into the Various Artists group (mirrors the DestinationPathResolver routing).
        var song = Song(album: "Greatest Hits", albumArtist: "Queen", isCompilation: true);

        Assert.Equal("queen", AlbumGroupKey.For(song)!.ArtistKey);
    }

    [Fact]
    public void For_MissingAlbumArtist_FallsBackToPrimaryArtist()
    {
        var a = Song(album: "Watch the Throne", albumArtist: null, artist: "JAY-Z & Kanye West");
        var b = Song(album: "Watch the Throne", albumArtist: "JAY-Z");

        Assert.Equal(AlbumGroupKey.For(a), AlbumGroupKey.For(b));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("(((")] // normalizes to empty
    public void For_NoUsableAlbum_ReturnsNull(string? album)
    {
        Assert.Null(AlbumGroupKey.For(Song(album: album)));
    }

    [Fact]
    public void For_DifferentOwners_DistinctKeys()
    {
        var a = Song(album: "Graduation");
        var b = Song(album: "Graduation");
        b.OwnerUserId = Guid.NewGuid();

        Assert.NotEqual(AlbumGroupKey.For(a), AlbumGroupKey.For(b));
    }

    [Fact]
    public void ComputeKeys_MatchSongSideKeys_ForEndpointLookups()
    {
        // The rebuild endpoint computes keys from the display artist/album the album view shows —
        // those must land in the same group as the song rows themselves.
        var song = Song(album: "Graduation (Deluxe)", albumArtist: "Kanye West");
        var key = AlbumGroupKey.For(song)!;

        Assert.Equal(key.ArtistKey, AlbumGroupKey.ComputeArtistKey("Kanye West"));
        Assert.Equal(key.AlbumKey, AlbumGroupKey.ComputeAlbumKey("Graduation (Deluxe)"));
    }

    private static SongMetadata Song(
        string? album,
        int? year = null,
        string? albumArtist = "Kanye West",
        string? artist = "Kanye West",
        bool isCompilation = false) => new()
    {
        OwnerUserId = WellKnownUsers.OwnerId,
        SourcePath = "/src/x.flac",
        FileName = "x.flac",
        Extension = ".flac",
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Album = album,
        Year = year,
        AlbumArtist = albumArtist,
        Artist = artist,
        IsCompilation = isCompilation,
    };
}
