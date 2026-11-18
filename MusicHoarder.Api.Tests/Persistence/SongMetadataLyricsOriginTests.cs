using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Persistence;

public class SongMetadataLyricsOriginTests
{
    private static SongMetadata NewSong() => new()
    {
        SourcePath = "/src/a.flac",
        FileName = "a.flac",
        Extension = ".flac",
        FileSizeBytes = 1L,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Artist = "Artist",
        Title = "Title",
    };

    [Fact]
    public void NewSong_IsLrclib()
    {
        Assert.Equal(LyricsOrigin.Lrclib, NewSong().LyricsOrigin);
    }

    [Fact]
    public void GeniusResult_RecordsItsPageAndLeavesLrclibFieldsAlone()
    {
        var song = NewSong();
        song.LrclibId = "5";

        song.ApplyLyricsResult(null, "words", false, origin: LyricsOrigin.Genius, geniusSongId: 42);

        Assert.Equal(LyricsOrigin.Genius, song.LyricsOrigin);
        Assert.Equal(42, song.GeniusSongId);
        Assert.Equal("5", song.LrclibId);
    }

    [Fact]
    public void LrclibResult_ClearsAnEarlierGeniusPage()
    {
        var song = NewSong();
        song.ApplyLyricsResult(null, "words", false, origin: LyricsOrigin.Genius, geniusSongId: 42);

        Assert.True(song.TryApplyLyricsUpgrade("[00:01.00]words", "words", false, lrclibId: 9));

        Assert.Equal(LyricsOrigin.Lrclib, song.LyricsOrigin);
        Assert.Null(song.GeniusSongId);
    }

    [Fact]
    public void ResetAndNotFound_ForgetTheGeniusOrigin()
    {
        var reset = NewSong();
        reset.ApplyLyricsResult(null, "words", false, origin: LyricsOrigin.Genius, geniusSongId: 42);
        reset.ResetLyrics();

        var notFound = NewSong();
        notFound.ApplyLyricsResult(null, "words", false, origin: LyricsOrigin.Genius, geniusSongId: 42);
        notFound.MarkLyricsNotFound();

        foreach (var song in new[] { reset, notFound })
        {
            Assert.Equal(LyricsOrigin.Lrclib, song.LyricsOrigin);
            Assert.Null(song.GeniusSongId);
        }
    }
}
