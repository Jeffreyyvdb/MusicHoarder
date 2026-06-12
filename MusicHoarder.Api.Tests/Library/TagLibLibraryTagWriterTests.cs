using MusicHoarder.Api.Library;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Library;

/// <summary>
/// Round-trips real audio files through <see cref="TagLibLibraryTagWriter"/> (TagLib hits the real
/// filesystem, so these use temp copies of the committed silent fixtures rather than MockFileSystem).
/// </summary>
public class TagLibLibraryTagWriterTests : IDisposable
{
    private static readonly string FixtureDir =
        Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private readonly List<string> tempFiles = [];

    [Fact]
    public async Task WriteTags_Mp3_ForcesId3v24()
    {
        var path = CopyFixture("silence.mp3");
        var song = BasicSong();
        await new TagLibLibraryTagWriter().WriteTagsAsync(path, song, Identity(song));

        using var file = TagLib.File.Create(path);
        var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2);
        Assert.Equal(4, id3.Version);
    }

    [Fact]
    public async Task WriteTags_Mp3_WritesMultiValueArtistsAndCleanAlbumArtist()
    {
        var path = CopyFixture("silence.mp3");
        var song = BasicSong();
        song.Artist = "Alice feat. Bob";
        song.Artists = "Alice; Bob";
        song.AlbumArtist = "Alice";

        await new TagLibLibraryTagWriter().WriteTagsAsync(path, song, Identity(song));

        using var file = TagLib.File.Create(path);
        var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2);

        var artists = TagLib.Id3v2.UserTextInformationFrame.Get(id3, "ARTISTS", false);
        Assert.NotNull(artists);
        Assert.Equal(["Alice", "Bob"], artists!.Text);

        // The album-artist must stay the bare primary so the album never fragments.
        Assert.Equal(["Alice"], file.Tag.AlbumArtists);
        // The display artist keeps the full "feat." credit.
        Assert.Equal("Alice feat. Bob", file.Tag.JoinedPerformers);
    }

    [Fact]
    public async Task WriteTags_Mp3_WritesMusicBrainzIdsInCorrectSlots()
    {
        var path = CopyFixture("silence.mp3");
        var song = BasicSong();
        song.MusicBrainzId = "rec-111";              // recording id
        song.MusicBrainzReleaseId = "rel-222";
        song.MusicBrainzReleaseGroupId = "rg-333";
        song.AlbumArtistMusicBrainzId = "aa-444";

        await new TagLibLibraryTagWriter().WriteTagsAsync(path, song, Identity(song));

        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        // Gotcha: MusicBrainzTrackId is the RECORDING id field.
        Assert.Equal("rec-111", tag.MusicBrainzTrackId);
        Assert.Equal("rel-222", tag.MusicBrainzReleaseId);
        Assert.Equal("rg-333", tag.MusicBrainzReleaseGroupId);
        Assert.Equal("aa-444", tag.MusicBrainzReleaseArtistId);
    }

    [Fact]
    public async Task WriteTags_Mp3_Compilation_SetsVariousArtistsAndTcmp()
    {
        var path = CopyFixture("silence.mp3");
        var song = BasicSong();
        song.Artist = "Some Performer";
        song.AlbumArtist = "Some Performer";
        song.IsCompilation = true;

        await new TagLibLibraryTagWriter().WriteTagsAsync(path, song, Identity(song));

        using var file = TagLib.File.Create(path);
        var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2);

        Assert.Equal(["Various Artists"], file.Tag.AlbumArtists);
        Assert.Equal("89ad4ac3-39f7-470e-963a-56509c546377", file.Tag.MusicBrainzReleaseArtistId);

        var tcmp = TagLib.Id3v2.TextInformationFrame.Get(id3, "TCMP", false);
        Assert.NotNull(tcmp);
        Assert.Equal(["1"], tcmp!.Text);
    }

    [Fact]
    public async Task WriteTags_Mp3_WritesDiscAndTrackTotals()
    {
        var path = CopyFixture("silence.mp3");
        var song = BasicSong();
        song.TrackNumber = 5;
        song.TotalTracks = 12;
        song.DiscNumber = 2;
        song.TotalDiscs = 2;

        await new TagLibLibraryTagWriter().WriteTagsAsync(path, song, Identity(song));

        using var file = TagLib.File.Create(path);
        Assert.Equal(5u, file.Tag.Track);
        Assert.Equal(12u, file.Tag.TrackCount);
        Assert.Equal(2u, file.Tag.Disc);
        Assert.Equal(2u, file.Tag.DiscCount);
    }

    [Fact]
    public async Task WriteTags_EmbedsSyncedLyrics_PreferredOverPlain()
    {
        // Lyrics present on the row at build time must land in the file — together with the
        // lyrics-wait build gate and the rebuild-on-lyrics-change interceptor, this closes the
        // "built file missing the lyrics the DB holds" race for good.
        var path = CopyFixture("silence.mp3");
        var song = BasicSong();
        song.SyncedLyrics = "[00:01.00] first line";
        song.PlainLyrics = "first line";

        await new TagLibLibraryTagWriter().WriteTagsAsync(path, song, Identity(song));

        using var file = TagLib.File.Create(path);
        Assert.Equal("[00:01.00] first line", file.Tag.Lyrics);
    }

    [Fact]
    public async Task WriteTags_EmbedsPlainLyrics_WhenNoSyncedLyrics()
    {
        var path = CopyFixture("silence.mp3");
        var song = BasicSong();
        song.PlainLyrics = "just the plain text";

        await new TagLibLibraryTagWriter().WriteTagsAsync(path, song, Identity(song));

        using var file = TagLib.File.Create(path);
        Assert.Equal("just the plain text", file.Tag.Lyrics);
    }

    [Fact]
    public async Task WriteTags_Flac_WritesMultiValueArtistsAndReleaseType()
    {
        var path = CopyFixture("silence.flac");
        var song = BasicSong();
        song.Artists = "Alice; Bob";
        song.ReleaseTypes = "album; compilation";

        await new TagLibLibraryTagWriter().WriteTagsAsync(path, song, Identity(song));

        using var file = TagLib.File.Create(path);
        var xiph = (TagLib.Ogg.XiphComment)file.GetTag(TagLib.TagTypes.Xiph);

        Assert.Equal(["Alice", "Bob"], xiph.GetField("ARTISTS"));
        Assert.Equal(["album", "compilation"], xiph.GetField("RELEASETYPE"));
    }

    [Fact]
    public async Task WriteTags_Mp3_PreservesEmbeddedPicture()
    {
        // Embedded art must survive the build: the byte-copy keeps it and the tag writer never
        // touches tag.Pictures, so TagLib's re-save leaves the picture intact. This locks that in.
        var path = CopyFixture("silence.mp3");
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 };
        using (var seed = TagLib.File.Create(path))
        {
            seed.Tag.Pictures =
            [
                new TagLib.Picture(new TagLib.ByteVector(pngBytes))
                {
                    MimeType = "image/png",
                    Type = TagLib.PictureType.FrontCover,
                }
            ];
            seed.Save();
        }

        await new TagLibLibraryTagWriter().WriteTagsAsync(path, BasicSong(), Identity(BasicSong()));

        using var file = TagLib.File.Create(path);
        var picture = Assert.Single(file.Tag.Pictures);
        Assert.Equal(pngBytes, picture.Data.Data);
    }

    [Fact]
    public async Task WriteTags_Flac_DoesNotCreateId3Tag()
    {
        var path = CopyFixture("silence.flac");
        await new TagLibLibraryTagWriter().WriteTagsAsync(path, BasicSong(), Identity(BasicSong()));

        using var file = TagLib.File.Create(path);
        // ID3-on-FLAC is non-spec and breaks some players; we must never create one.
        Assert.Null(file.GetTag(TagLib.TagTypes.Id3v2));
    }

    [Fact]
    public async Task WriteTags_UsesAlbumIdentityForAlbumFields_NotSongRow()
    {
        // The whole point of reconciliation: album-IDENTITY tags come from the shared identity, while
        // track-level tags still come from the song. A song that enriched to a different release must
        // be written with the album's elected identity so the on-disk album stays together.
        var path = CopyFixture("silence.mp3");
        var song = BasicSong();
        song.Album = "Song Album";
        song.Year = 1999;
        song.MusicBrainzReleaseId = "rel-song";
        song.MusicBrainzReleaseGroupId = "rg-song";
        // Track-level fields that must survive from the song:
        song.Title = "Track Title";
        song.TrackNumber = 7;
        song.DiscNumber = 1;
        song.Isrc = "USABC1234567";
        song.MusicBrainzId = "rec-song";

        var identity = new AlbumIdentity(
            Album: "Album Album",
            AlbumArtist: "Album Artist",
            Year: 2001,
            IsCompilation: false,
            TotalDiscs: 1,
            ReleaseTypePrimary: "album",
            ReleaseTypes: "album",
            MusicBrainzReleaseId: "rel-album",
            MusicBrainzReleaseGroupId: "rg-album",
            AlbumArtistMusicBrainzId: "aa-album");

        await new TagLibLibraryTagWriter().WriteTagsAsync(path, song, identity);

        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        // Album identity wins for album-level fields.
        Assert.Equal("Album Album", tag.Album);
        Assert.Equal(2001u, tag.Year);
        Assert.Equal("rel-album", tag.MusicBrainzReleaseId);
        Assert.Equal("rg-album", tag.MusicBrainzReleaseGroupId);
        Assert.Equal("aa-album", tag.MusicBrainzReleaseArtistId);
        // Track-level fields still come straight from the song.
        Assert.Equal("Track Title", tag.Title);
        Assert.Equal(7u, tag.Track);
        Assert.Equal("USABC1234567", tag.ISRC);
        Assert.Equal("rec-song", tag.MusicBrainzTrackId);
    }

    [Fact]
    public async Task WriteTags_Compilation_DrivenByAlbumIdentity_NotSong()
    {
        // A single mis-enriched track (IsCompilation=false on the row) is still written as Various
        // Artists when the elected album identity says the album is a compilation.
        var path = CopyFixture("silence.mp3");
        var song = BasicSong();
        song.Artist = "Some Performer";
        song.AlbumArtist = "Some Performer";
        song.IsCompilation = false;

        var identity = AlbumIdentity.FromSong(song) with { IsCompilation = true };

        await new TagLibLibraryTagWriter().WriteTagsAsync(path, song, identity);

        using var file = TagLib.File.Create(path);
        var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2);
        Assert.Equal(["Various Artists"], file.Tag.AlbumArtists);
        var tcmp = TagLib.Id3v2.TextInformationFrame.Get(id3, "TCMP", false);
        Assert.NotNull(tcmp);
        Assert.Equal(["1"], tcmp!.Text);
    }

    // The identity that mirrors a song's own album fields — what reconciliation produces for a
    // single-member album, so existing per-song assertions hold unchanged.
    private static AlbumIdentity Identity(SongMetadata song) => AlbumIdentity.FromSong(song);

    private static SongMetadata BasicSong() => new()
    {
        OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
        SourcePath = "/source/song",
        FileSizeBytes = 1,
        FileName = "song",
        Extension = ".mp3",
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Title = "A Title",
        Album = "An Album",
        Artist = "An Artist",
        AlbumArtist = "An Artist",
        Year = 2020,
        TrackNumber = 1,
    };

    private string CopyFixture(string fixtureName)
    {
        var source = Path.Combine(FixtureDir, fixtureName);
        var dest = Path.Combine(
            Path.GetTempPath(),
            $"mh-tagtest-{Guid.NewGuid():N}{Path.GetExtension(fixtureName)}");
        File.Copy(source, dest, overwrite: true);
        tempFiles.Add(dest);
        return dest;
    }

    public void Dispose()
    {
        foreach (var f in tempFiles)
        {
            try { File.Delete(f); } catch { /* best effort */ }
        }
    }
}
