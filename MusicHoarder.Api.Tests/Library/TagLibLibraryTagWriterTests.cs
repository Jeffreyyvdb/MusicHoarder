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
        await new TagLibLibraryTagWriter().WriteTagsAsync(path, BasicSong());

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

        await new TagLibLibraryTagWriter().WriteTagsAsync(path, song);

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

        await new TagLibLibraryTagWriter().WriteTagsAsync(path, song);

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

        await new TagLibLibraryTagWriter().WriteTagsAsync(path, song);

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

        await new TagLibLibraryTagWriter().WriteTagsAsync(path, song);

        using var file = TagLib.File.Create(path);
        Assert.Equal(5u, file.Tag.Track);
        Assert.Equal(12u, file.Tag.TrackCount);
        Assert.Equal(2u, file.Tag.Disc);
        Assert.Equal(2u, file.Tag.DiscCount);
    }

    [Fact]
    public async Task WriteTags_Flac_WritesMultiValueArtistsAndReleaseType()
    {
        var path = CopyFixture("silence.flac");
        var song = BasicSong();
        song.Artists = "Alice; Bob";
        song.ReleaseTypes = "album; compilation";

        await new TagLibLibraryTagWriter().WriteTagsAsync(path, song);

        using var file = TagLib.File.Create(path);
        var xiph = (TagLib.Ogg.XiphComment)file.GetTag(TagLib.TagTypes.Xiph);

        Assert.Equal(["Alice", "Bob"], xiph.GetField("ARTISTS"));
        Assert.Equal(["album", "compilation"], xiph.GetField("RELEASETYPE"));
    }

    [Fact]
    public async Task WriteTags_Flac_DoesNotCreateId3Tag()
    {
        var path = CopyFixture("silence.flac");
        await new TagLibLibraryTagWriter().WriteTagsAsync(path, BasicSong());

        using var file = TagLib.File.Create(path);
        // ID3-on-FLAC is non-spec and breaks some players; we must never create one.
        Assert.Null(file.GetTag(TagLib.TagTypes.Id3v2));
    }

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
