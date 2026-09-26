using MusicHoarder.Api.Enrichment.Providers;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

/// <summary>
/// Exercises the shared catalog artist / album-artist derivation in isolation — the rule that used to
/// be copy-pasted (comment and all) into the Deezer / Apple Music / Spotify <c>BuildResult</c> methods.
/// </summary>
public class CatalogResultArtistsTests
{
    [Fact]
    public void Resolve_CuratedAlbumArtist_IsPreservedOverTrackCredit()
    {
        // Guest-performed track: track credit is the featured guest; the curated album-artist must win
        // so the album isn't split apart.
        var song = SongWith(artist: "Big Sean", albumArtist: "Kanye West");

        var resolved = CatalogResultArtists.Resolve(song, trackArtist: "Big Sean");

        Assert.Equal("Big Sean", resolved.Artist);
        Assert.Equal("Kanye West", resolved.AlbumArtist);
    }

    [Fact]
    public void Resolve_CommaName_AlbumArtistNotTruncated()
    {
        // "Tyler, The Creator" must not be truncated to "Tyler" (GetPrimaryArtist splits on ", ").
        var song = SongWith(artist: "Tyler, The Creator", albumArtist: "Tyler, The Creator");

        var resolved = CatalogResultArtists.Resolve(song, trackArtist: "Tyler, The Creator");

        Assert.Equal("Tyler, The Creator", resolved.AlbumArtist);
    }

    [Fact]
    public void Resolve_NoCuratedAlbumArtist_FallsBackToTrackPrimary()
    {
        var song = SongWith(artist: "Juice WRLD", albumArtist: null);

        var resolved = CatalogResultArtists.Resolve(song, trackArtist: "Juice WRLD");

        Assert.Equal("Juice WRLD", resolved.AlbumArtist);
    }

    [Fact]
    public void Resolve_BlankTrackArtist_FallsBackToSongArtist()
    {
        var song = SongWith(artist: "Juice WRLD", albumArtist: null);

        var resolved = CatalogResultArtists.Resolve(song, trackArtist: "  ");

        Assert.Equal("Juice WRLD", resolved.Artist);
        Assert.Equal("Juice WRLD", resolved.AlbumArtist);
    }

    [Theory]
    [InlineData("Tyler, The Creator", "Tyler, The Creator", "Tyler, The Creator")]
    [InlineData("Chase & Status, Plan B", "Chase & Status; Plan B", "Chase & Status")]
    [InlineData("Juice WRLD, Future", "Juice WRLD; Future", "Juice WRLD")]
    public void Resolve_NoCuratedAlbumArtist_TakesTheLeadFromTheDiscreteList(
        string trackArtist, string trackArtists, string expected)
    {
        // Spotify's display credit is its artists[] joined with ", ", which a re-parse cannot tell
        // from a name: it cut "Tyler, The Creator" to "Tyler" and "Chase & Status" to "Chase".
        var song = SongWith(artist: null, albumArtist: null);

        var resolved = CatalogResultArtists.Resolve(song, trackArtist, trackArtists);

        Assert.Equal(trackArtist, resolved.Artist);
        Assert.Equal(expected, resolved.AlbumArtist);
    }

    [Fact]
    public void Resolve_DiscreteList_NeverOverridesTheCuratedAlbumArtist()
    {
        var song = SongWith(artist: "Big Sean", albumArtist: "Kanye West");

        var resolved = CatalogResultArtists.Resolve(song, "Big Sean, Kanye West", "Big Sean; Kanye West");

        Assert.Equal("Kanye West", resolved.AlbumArtist);
    }

    [Fact]
    public void Resolve_BlankTrackArtist_IgnoresTheDiscreteList()
    {
        // The list describes the catalog's credit; when the song's own artist stands in for a blank
        // one, the album-artist must come from that artist, not from a list it never produced.
        var song = SongWith(artist: "Juice WRLD", albumArtist: null);

        var resolved = CatalogResultArtists.Resolve(song, trackArtist: " ", trackArtists: "Someone Else");

        Assert.Equal("Juice WRLD", resolved.Artist);
        Assert.Equal("Juice WRLD", resolved.AlbumArtist);
    }

    private static SongMetadata SongWith(string? artist, string? albumArtist) => new()
    {
        OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
        SourcePath = "/s/a.mp3",
        FileName = "a.mp3",
        Extension = ".mp3",
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Artist = artist,
        AlbumArtist = albumArtist,
        EnrichmentStatus = EnrichmentStatus.Pending,
    };
}
