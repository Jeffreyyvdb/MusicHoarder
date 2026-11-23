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
    public void LrclibSyncedOnlyUpgrade_DoesNotKeepGeniusPlainTextUnderTheLrclibLabel()
    {
        var song = NewSong();
        song.ApplyLyricsResult(null, "genius words", false, origin: LyricsOrigin.Genius, geniusSongId: 42);

        Assert.True(song.TryApplyLyricsUpgrade("[00:01.00]lrclib words", null, false, lrclibId: 9));

        Assert.Equal(LyricsOrigin.Lrclib, song.LyricsOrigin);
        Assert.Equal("[00:01.00]lrclib words", song.SyncedLyrics);
        Assert.Null(song.PlainLyrics);
    }

    [Fact]
    public void LrclibPlainText_ReplacesGeniusPlainText()
    {
        var song = NewSong();
        song.ApplyLyricsResult(null, "genius words", false, origin: LyricsOrigin.Genius, geniusSongId: 42);

        Assert.True(song.TryApplyLyricsUpgrade(null, "lrclib words", false, lrclibId: 9));

        Assert.Equal("lrclib words", song.PlainLyrics);
        Assert.Equal(LyricsOrigin.Lrclib, song.LyricsOrigin);
        Assert.Null(song.GeniusSongId);
    }

    [Fact]
    public void GeniusPlainText_NeverReplacesLrclibPlainText()
    {
        var song = NewSong();
        song.ApplyLyricsResult(null, "lrclib words", false, lrclibId: 9);

        Assert.False(song.TryApplyLyricsUpgrade(null, "genius words", false, origin: LyricsOrigin.Genius, geniusSongId: 42));
        Assert.False(song.TryApplyLyricsUpgrade(null, "other lrclib words", false, lrclibId: 10));

        Assert.Equal("lrclib words", song.PlainLyrics);
        Assert.Equal(LyricsOrigin.Lrclib, song.LyricsOrigin);
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
