using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Persistence;

/// <summary>
/// The AI disclosure label. Its whole job is to be honest about who wrote the words on the screen, so the
/// tests are mostly about the boundary between "a machine chose these words" and "a machine only moved
/// these timestamps" — two very different claims that must never be conflated.
/// </summary>
public class LyricsProvenanceTests
{
    private static SongMetadata Song() => new()
    {
        SourcePath = "/s/song.flac",
        FileName = "song.flac",
        Extension = ".flac",
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Artist = "Artist",
        Title = "Title",
    };

    [Fact]
    public void plain_lrclib_lyrics_carry_no_ai_label()
    {
        var song = Song();
        song.ApplyLyricsResult("[00:01.00]a line", "a line", instrumental: false);

        Assert.Equal(LyricsProvenance.Human, song.LyricsProvenance);
    }

    [Fact]
    public void a_transcription_aligned_to_the_official_lyrics_is_ai_enhanced()
    {
        // Same words LRCLIB has, re-timed against the audio. The lyric is still the human one.
        var song = Song();
        song.ApplyLyricsResult("[00:01.00]a line", "a line", instrumental: false);
        song.ApplyTranscriptionResult("[00:03.00]a line", "a line", "whisper-large-v3", alignedToReference: true);
        song.PreferredLyricsSource = PreferredLyricsSource.Transcribed;

        Assert.Equal(LyricsProvenance.AiEnhanced, song.LyricsProvenance);
    }

    [Fact]
    public void a_transcription_the_aligner_could_not_place_is_ai_generated()
    {
        // The words here are Whisper's guess at what is being sung, not anybody's lyric sheet.
        var song = Song();
        song.ApplyTranscriptionResult("[00:03.00]what the model heard", "what the model heard", "whisper-large-v3");

        Assert.Equal(LyricsProvenance.AiGenerated, song.LyricsProvenance);
    }

    [Fact]
    public void a_transcription_shown_only_because_lrclib_had_nothing_is_still_labelled()
    {
        // Never promoted, never chosen — it is displayed because it is all there is. The reader still needs
        // to know a machine wrote it.
        var song = Song();
        song.ApplyTranscriptionResult("[00:03.00]what the model heard", "what the model heard", "whisper-large-v3");

        Assert.Equal(PreferredLyricsSource.Lrclib, song.PreferredLyricsSource);
        Assert.Equal(LyricsProvenance.AiGenerated, song.LyricsProvenance);
    }

    [Fact]
    public void human_lyrics_re_timed_by_the_probe_are_ai_enhanced()
    {
        var song = Song();
        song.ApplyLyricsResult("[00:01.00]a line", "a line", instrumental: false);
        song.ApplyLyricsSyncOffset("[00:16.00]a line", offsetMs: 15000, confidence: 0.9);

        Assert.Equal(LyricsProvenance.AiEnhanced, song.LyricsProvenance);
    }

    [Fact]
    public void a_transcription_kept_only_for_comparison_does_not_relabel_the_lyrics_on_screen()
    {
        // The viewer is still showing LRCLIB's lines, so the badge must describe those, not the alternate
        // version sitting beside them in the compare view.
        var song = Song();
        song.ApplyLyricsResult("[00:01.00]a line", "a line", instrumental: false);
        song.ApplyTranscriptionResult("[00:03.00]what the model heard", "what the model heard", "whisper-large-v3");

        Assert.Equal(LyricsProvenance.Human, song.LyricsProvenance);
    }

    [Fact]
    public void re_fetching_lyrics_drops_a_stale_repair_and_its_label()
    {
        var song = Song();
        song.ApplyLyricsResult("[00:01.00]a line", "a line", instrumental: false);
        song.ApplyLyricsSyncOffset("[00:16.00]a line", offsetMs: 15000, confidence: 0.9);

        // New text from LRCLIB: the old offset described the old lines and means nothing now.
        song.ApplyLyricsResult("[00:02.00]a different line", "a different line", instrumental: false);

        Assert.Null(song.LyricsSyncOffsetMs);
        Assert.Equal(LyricsSyncStatus.NotChecked, song.LyricsSyncStatus);
        Assert.Equal(LyricsProvenance.Human, song.LyricsProvenance);
    }

    [Fact]
    public void a_second_repair_accumulates_onto_the_first()
    {
        // Each probe measures against the already-shifted text, so the stored offset must be the total
        // drift from LRCLIB's original, not just the latest nudge.
        var song = Song();
        song.ApplyLyricsResult("[00:01.00]a line", "a line", instrumental: false);
        song.ApplyLyricsSyncOffset("[00:11.00]a line", offsetMs: 10000, confidence: 0.9);
        song.ApplyLyricsSyncOffset("[00:13.00]a line", offsetMs: 2000, confidence: 0.9);

        Assert.Equal(12000, song.LyricsSyncOffsetMs);
    }

    [Fact]
    public void resetting_a_transcription_clears_its_alignment_claim()
    {
        var song = Song();
        song.ApplyTranscriptionResult("[00:03.00]a line", "a line", "whisper-large-v3", alignedToReference: true);
        song.ResetTranscription();

        Assert.False(song.TranscriptionAlignedToReference);
    }
}
