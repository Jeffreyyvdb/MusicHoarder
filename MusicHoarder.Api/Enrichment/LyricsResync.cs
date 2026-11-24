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
    public static TranscriptionApplied Apply(SongMetadata song, TranscriptionResult result)
    {
        // The transcriber looks the words up on LRCLIB itself when the song holds none. Words it found and
        // aligned to are the song's official lyrics now; without this the next lyrics re-check would bring
        // the same words as new and the AI sync pass would transcribe the song again to align them.
        if (result.AlignedToReference && string.IsNullOrWhiteSpace(song.PlainLyrics))
            song.TryApplyLyricsUpgrade(null, result.PlainLyrics, instrumental: false);

        // Stored separately from SyncedLyrics/PlainLyrics so it never clobbers the LRCLIB version.
        song.ApplyTranscriptionResult(result.SyncedLyrics, result.PlainLyrics, result.Model, result.AlignedToReference);

        // A re-sync of the song's OWN official lyrics (forced alignment against the LRCLIB text) is the same
        // words with better timing, so it becomes the default automatically — that is the entire point of a
        // re-sync on a track that already has lyrics. A transcription with no reference text is the AI's
        // guess at the words and is never promoted; the user picks that one from the compare view.
        var promoted = false;
        if (result.AlignedToReference
            && !string.IsNullOrWhiteSpace(song.TranscribedSyncedLyrics)
            && song.PreferredLyricsSource != PreferredLyricsSource.Transcribed)
        {
            song.PreferredLyricsSource = PreferredLyricsSource.Transcribed;
            promoted = true;
        }

        // If the AI version is this song's chosen default, the file's effective lyrics just changed — re-tag
        // the built destination so it reflects the fresh transcription.
        var retagQueued = false;
        if (song.PreferredLyricsSource == PreferredLyricsSource.Transcribed
            && song.LibraryBuildStatus == LibraryBuildStatus.Done)
        {
            song.RequeueForRetag();
            retagQueued = true;
        }

        return new TranscriptionApplied(promoted, retagQueued);
    }
}
