using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Persistence;

public class SongMetadataLyricsTranslationTests
{
    private static SongMetadata NewSong() => new()
    {
        SourcePath = "/src/a.flac",
        FileName = "a.flac",
        Extension = ".flac",
        FileSizeBytes = 1234L,
        LastModifiedUtc = new DateTime(2026, 1, 1),
        IndexedAtUtc = new DateTime(2026, 1, 1),
    };

    private static SongMetadata SongWithTranslation()
    {
        var song = NewSong();
        song.ApplyLyricsResult("[00:01.00]حبيبي", "حبيبي", instrumental: false);
        song.ApplyLyricsTranslationResult(
            "[00:01.00]7abibi", "7abibi", "[00:01.00]My darling", "My darling", "ar", "test/model",
            SongMetadata.ComputeLyricsFingerprint(song.CurrentLyricsForTranslation));
        return song;
    }

    [Fact]
    public void ApplyLyricsTranslationResult_SetsAllFields()
    {
        var song = SongWithTranslation();

        Assert.Equal(LyricsTranslationStatus.Completed, song.LyricsTranslationStatus);
        Assert.Equal("[00:01.00]7abibi", song.RomanizedSyncedLyrics);
        Assert.Equal("7abibi", song.RomanizedPlainLyrics);
        Assert.Equal("[00:01.00]My darling", song.TranslatedSyncedLyrics);
        Assert.Equal("My darling", song.TranslatedPlainLyrics);
        Assert.Equal("ar", song.DetectedLyricsLanguage);
        Assert.Equal("test/model", song.LyricsTranslationModel);
        Assert.NotNull(song.LyricsTranslatedAtUtc);
        Assert.Null(song.LyricsTranslationError);
    }

    [Fact]
    public void ApplyLyricsTranslationResult_NormalizesWhitespaceToNull_AndAllowsEnglishOutcome()
    {
        var song = NewSong();

        song.ApplyLyricsTranslationResult("  ", null, "", null, "en", "test/model");

        Assert.Equal(LyricsTranslationStatus.Completed, song.LyricsTranslationStatus);
        Assert.Null(song.RomanizedSyncedLyrics);
        Assert.Null(song.TranslatedSyncedLyrics);
        Assert.Equal("en", song.DetectedLyricsLanguage);
    }

    [Fact]
    public void MarkLyricsTranslationFailed_TruncatesLongErrors()
    {
        var song = NewSong();

        song.MarkLyricsTranslationFailed(new string('x', 5000));

        Assert.Equal(LyricsTranslationStatus.Failed, song.LyricsTranslationStatus);
        Assert.NotNull(song.LyricsTranslationError);
        Assert.True(song.LyricsTranslationError!.Length < 5000);
    }

    [Fact]
    public void ResetLyrics_ClearsTranslation()
    {
        var song = SongWithTranslation();

        song.ResetLyrics();

        Assert.Equal(LyricsTranslationStatus.NotRequested, song.LyricsTranslationStatus);
        Assert.Null(song.RomanizedSyncedLyrics);
        Assert.Null(song.RomanizedPlainLyrics);
        Assert.Null(song.TranslatedSyncedLyrics);
        Assert.Null(song.TranslatedPlainLyrics);
        Assert.Null(song.DetectedLyricsLanguage);
        Assert.Null(song.LyricsTranslatedAtUtc);
        Assert.Null(song.LyricsTranslationModel);
    }

    [Fact]
    public void ResetEnrichment_ClearsTranslationTransitively()
    {
        var song = SongWithTranslation();
        song.CaptureOriginalMetadata();

        song.ResetEnrichment(restoreOriginal: true);

        Assert.Equal(LyricsTranslationStatus.NotRequested, song.LyricsTranslationStatus);
        Assert.Null(song.RomanizedSyncedLyrics);
    }

    [Fact]
    public void ResetTranscription_DoesNotClearTranslation()
    {
        var song = SongWithTranslation();

        song.ResetTranscription();

        Assert.Equal(LyricsTranslationStatus.Completed, song.LyricsTranslationStatus);
        Assert.Equal("[00:01.00]7abibi", song.RomanizedSyncedLyrics);
    }

    // --- Staleness ---

    [Fact]
    public void Translation_IsFresh_WhenLyricsUnchanged()
    {
        var song = SongWithTranslation();

        Assert.False(song.IsLyricsTranslationStale);
    }

    [Fact]
    public void Translation_BecomesStale_WhenPreferredSourceFlipsToTranscription()
    {
        var song = SongWithTranslation();
        song.ApplyTranscriptionResult("[00:01.00]different transcribed line", "different transcribed line", "whisper-1");
        Assert.False(song.IsLyricsTranslationStale); // LRCLIB still preferred → display lyrics unchanged

        song.PreferredLyricsSource = PreferredLyricsSource.Transcribed;

        Assert.True(song.IsLyricsTranslationStale);
    }

    [Fact]
    public void Translation_BecomesStale_WhenTranscriptionReplacesOnlyLyrics()
    {
        // Song with NO LRCLIB lyrics: the transcription IS the display source. Translating it,
        // then re-transcribing to different text, must flag staleness.
        var song = NewSong();
        song.ApplyTranscriptionResult("[00:01.00]first take", "first take", "whisper-1");
        song.ApplyLyricsTranslationResult(
            "[00:01.00]first take", "first take", "[00:01.00]first take", "first take", "es", "test/model",
            SongMetadata.ComputeLyricsFingerprint(song.CurrentLyricsForTranslation));
        Assert.False(song.IsLyricsTranslationStale);

        song.ApplyTranscriptionResult("[00:01.00]second take", "second take", "whisper-1");

        Assert.True(song.IsLyricsTranslationStale);
    }

    [Fact]
    public void Translation_WithoutSourceHash_IsNeverStale()
    {
        // Pre-existing rows translated before hash tracking (or hash omitted) stay usable.
        var song = NewSong();
        song.ApplyLyricsResult("[00:01.00]hola", "hola", instrumental: false);
        song.ApplyLyricsTranslationResult("[00:01.00]OH-lah", "OH-lah", "[00:01.00]hello", "hello", "es", "test/model");

        song.ApplyLyricsResult("[00:01.00]adios", "adios", instrumental: false);

        Assert.False(song.IsLyricsTranslationStale);
    }

    [Fact]
    public void ComputeLyricsFingerprint_IsStableAndWhitespaceInsensitive()
    {
        Assert.Null(SongMetadata.ComputeLyricsFingerprint(null));
        Assert.Null(SongMetadata.ComputeLyricsFingerprint("   "));
        Assert.Equal(
            SongMetadata.ComputeLyricsFingerprint("abc"),
            SongMetadata.ComputeLyricsFingerprint("  abc  "));
        Assert.NotEqual(
            SongMetadata.ComputeLyricsFingerprint("abc"),
            SongMetadata.ComputeLyricsFingerprint("abd"));
    }
}
