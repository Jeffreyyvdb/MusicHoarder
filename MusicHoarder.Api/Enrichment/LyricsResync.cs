using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment;

/// <summary>What storing a transcription on a song changed, beyond the transcription itself.</summary>
/// <param name="Promoted">The transcription became the song's displayed (and tagged) lyrics.</param>
/// <param name="RetagQueued">The built file was queued to be re-tagged with the new lyrics.</param>
public readonly record struct TranscriptionApplied(bool Promoted, bool RetagQueued);

/// <summary>
/// Stores a finished transcription on a song and decides whether it becomes what the player shows. Shared by
/// the "Sync with AI" button and the timing sweep's automatic re-sync, so the two can never disagree about
/// when an AI version replaces the LRCLIB one.
/// </summary>
public static class LyricsResync
{
    /// <param name="wordsStampUtc">
    /// The song's <see cref="SongMetadata.LyricsTextChangedAtUtc"/> when the transcription started: which
    /// version of the official words it was aligned to. A lyrics re-check can bring new words while the audio
    /// is out, and a result made for the old ones must neither replace nor be shown over the new.
    /// </param>
    public static TranscriptionApplied Apply(SongMetadata song, TranscriptionResult result, DateTime? wordsStampUtc)
    {
        var wordsUnchanged = song.LyricsTextChangedAtUtc == wordsStampUtc;

        // The transcriber looks the words up on LRCLIB itself when the song holds none. Words it found and
        // aligned to are the song's official lyrics now; without this the next lyrics re-check would bring
        // the same words as new and the AI sync pass would transcribe the song again to align them.
        if (wordsUnchanged && result.AlignedToReference && string.IsNullOrWhiteSpace(song.PlainLyrics)
            && song.TryApplyLyricsUpgrade(null, result.PlainLyrics, instrumental: false, lrclibId: result.ReferenceLrclibId))
        {
            // That was an LRCLIB lookup, and History dates the words it found by it.
            song.LyricsLastAttemptedAtUtc = DateTime.UtcNow;
            wordsStampUtc = song.LyricsTextChangedAtUtc;
        }

        var wasAligned = song.TranscriptionAlignedToReference;
        // Stored separately from SyncedLyrics/PlainLyrics so it never clobbers the LRCLIB version.
        song.ApplyTranscriptionResult(result.SyncedLyrics, result.PlainLyrics, result.Model, result.AlignedToReference);
        song.TranscriptionWordsStampUtc = wordsStampUtc;

        // A re-sync of the song's OWN official lyrics (forced alignment against the LRCLIB text) is the same
        // words with better timing, so it becomes the default automatically — that is the entire point of a
        // re-sync on a track that already has lyrics. A transcription with no reference text is the AI's
        // guess at the words and is never promoted; the user picks that one from the compare view.
        var timesTheseWords = result.AlignedToReference && song.LyricsTextChangedAtUtc == wordsStampUtc;
        var promoted = false;
        var demoted = false;
        if (timesTheseWords
            && !string.IsNullOrWhiteSpace(song.TranscribedSyncedLyrics)
            && song.PreferredLyricsSource != PreferredLyricsSource.Transcribed)
        {
            song.PreferredLyricsSource = PreferredLyricsSource.Transcribed;
            promoted = true;
        }
        else if (!timesTheseWords && wasAligned
            && song.PreferredLyricsSource == PreferredLyricsSource.Transcribed
            && (!string.IsNullOrWhiteSpace(song.SyncedLyrics) || !string.IsNullOrWhiteSpace(song.PlainLyrics)))
        {
            // The AI version was chosen because it was the official words re-timed. This one is not (the
            // alignment failed, or the words changed meanwhile), so the official words take its place rather
            // than the AI's guess at them.
            song.PreferredLyricsSource = PreferredLyricsSource.Lrclib;
            demoted = true;
        }

        // The file's effective lyrics changed when the AI version is (or just stopped being) the default, so
        // the built destination is re-tagged.
        var retagQueued = false;
        if ((song.PreferredLyricsSource == PreferredLyricsSource.Transcribed || demoted)
            && song.LibraryBuildStatus == LibraryBuildStatus.Done)
        {
            song.RequeueForRetag();
            retagQueued = true;
        }

        return new TranscriptionApplied(promoted, retagQueued);
    }
}
