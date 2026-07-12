using MusicHoarder.Api.Library;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Library;

public class WrittenTagSetTests
{
    [Fact]
    public void From_MirrorsWriterArtistResolution()
    {
        // Same rules as TagLibLibraryTagWriter: a combined display credit without discrete data
        // yields NO Artists (the writer omits the ARTISTS frame); a ';' credit is the safe fallback.
        var combined = Song(artist: "21 Savage, Travis Scott & Metro Boomin");
        Assert.Null(WrittenTagSet.From(combined, AlbumIdentity.FromSong(combined)).Artists);

        var discrete = Song(artist: "Alice & Bob", artists: "Alice; Bob");
        Assert.Equal("Alice; Bob", WrittenTagSet.From(discrete, AlbumIdentity.FromSong(discrete)).Artists);

        var semicolon = Song(artist: "Alice; Bob");
        var set = WrittenTagSet.From(semicolon, AlbumIdentity.FromSong(semicolon));
        Assert.Equal("Alice; Bob", set.Artists);
        // The display artist is humanized to a single value, like the written ARTIST tag.
        Assert.Equal("Alice & Bob", set.Artist);
    }

    [Fact]
    public void Diff_SurfacesArtistsChange()
    {
        var before = Song(artist: "Alice & Bob");
        var after = Song(artist: "Alice & Bob", artists: "Alice; Bob", artistMbids: "mbid-a; mbid-b");

        var changes = WrittenTagSet.Diff(
            WrittenTagSet.From(before, AlbumIdentity.FromSong(before)),
            WrittenTagSet.From(after, AlbumIdentity.FromSong(after)));

        Assert.Contains(changes, c => c is { Field: "Artists", Old: null, New: "Alice; Bob" });
        Assert.Contains(changes, c => c is { Field: "ArtistMusicBrainzIds", Old: null, New: "mbid-a; mbid-b" });
    }

    [Fact]
    public void Diff_SurfacesDescriptiveFieldChanges()
    {
        var before = Song(artist: "Alice");
        var after = Song(artist: "Alice");
        after.Genre = "Jazz";
        after.Label = "Blue Note";
        after.CatalogNumber = "BN-1";
        after.Upc = "0123456789";
        after.ReleaseDate = "1959-08-17";
        after.Composer = "M. Davis";
        after.Copyright = "© 1959 Blue Note";

        var changes = WrittenTagSet.Diff(
            WrittenTagSet.From(before, AlbumIdentity.FromSong(before)),
            WrittenTagSet.From(after, AlbumIdentity.FromSong(after)));

        Assert.Contains(changes, c => c is { Field: "Genre", Old: null, New: "Jazz" });
        Assert.Contains(changes, c => c is { Field: "Label", Old: null, New: "Blue Note" });
        Assert.Contains(changes, c => c is { Field: "CatalogNumber", Old: null, New: "BN-1" });
        Assert.Contains(changes, c => c is { Field: "Upc", Old: null, New: "0123456789" });
        Assert.Contains(changes, c => c is { Field: "ReleaseDate", Old: null, New: "1959-08-17" });
        Assert.Contains(changes, c => c is { Field: "Composer", Old: null, New: "M. Davis" });
        Assert.Contains(changes, c => c is { Field: "Copyright", Old: null, New: "© 1959 Blue Note" });
    }

    [Fact]
    public void FromOriginal_UsesCapturedOriginalArtists()
    {
        var song = Song(artist: "Alice & Bob", artists: "Alice; Bob");
        song.OriginalMetadataCaptured = true;
        song.OriginalArtists = null; // pre-enrichment file had no discrete credit

        var current = WrittenTagSet.From(song, AlbumIdentity.FromSong(song));
        var original = WrittenTagSet.FromOriginal(song, current);

        Assert.Null(original.Artists);
        // No captured original for the id list — stays equal to current so a first build never
        // reports a spurious change for it.
        Assert.Equal(current.ArtistMusicBrainzIds, original.ArtistMusicBrainzIds);
    }

    private static SongMetadata Song(string artist, string? artists = null, string? artistMbids = null) => new()
    {
        OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
        SourcePath = "/x.flac",
        FileName = "x.flac",
        Extension = ".flac",
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Title = "Duet",
        Album = "Duets",
        Artist = artist,
        AlbumArtist = "Alice",
        Artists = artists,
        ArtistMusicBrainzIds = artistMbids,
        Year = 2020,
        TrackNumber = 1,
    };
}
