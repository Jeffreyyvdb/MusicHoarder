package com.musichoarder.app.data

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * The AI disclosure the lyrics viewer renders. It is a port of the API's `LyricsProvenance` rule and of
 * the web's `computeLyricsProvenance`, so the cases here mirror the ones pinned on both — a machine that
 * only moved timestamps must never be reported the same way as a machine that chose the words.
 */
class LyricsProvenanceTest {

    private fun response(
        synced: String? = null,
        plain: String? = null,
        transcribedSynced: String? = null,
        transcribedPlain: String? = null,
        preferredLyricsSource: String? = "Lrclib",
        lyricsProvenance: String? = null,
        transcriptionAlignedToReference: Boolean? = null,
        lyricsSyncOffsetMs: Int? = null,
    ) = LyricsResponse(
        id = 1,
        synced = synced,
        plain = plain,
        transcribedSynced = transcribedSynced,
        transcribedPlain = transcribedPlain,
        preferredLyricsSource = preferredLyricsSource,
        lyricsProvenance = lyricsProvenance,
        transcriptionAlignedToReference = transcriptionAlignedToReference,
        lyricsSyncOffsetMs = lyricsSyncOffsetMs,
    )

    @Test
    fun `the server's verdict wins when it sends one`() {
        val lyrics = response(synced = "[00:01.00]a line", lyricsProvenance = "AiEnhanced").toLyrics()

        assertEquals(LyricsProvenance.AiEnhanced, lyrics.provenance)
    }

    @Test
    fun `plain lrclib lyrics carry no label`() {
        val lyrics = response(synced = "[00:01.00]a line", plain = "a line").toLyrics()

        assertEquals(LyricsProvenance.Human, lyrics.provenance)
    }

    @Test
    fun `a transcription aligned to the official lyrics is ai enhanced`() {
        val lyrics = response(
            synced = "[00:01.00]a line",
            transcribedSynced = "[00:03.00]a line",
            preferredLyricsSource = "Transcribed",
            transcriptionAlignedToReference = true,
        ).toLyrics()

        assertEquals(LyricsProvenance.AiEnhanced, lyrics.provenance)
    }

    @Test
    fun `a transcription that could not be aligned is ai generated`() {
        val lyrics = response(
            transcribedSynced = "[00:03.00]what the model heard",
            preferredLyricsSource = "Transcribed",
            transcriptionAlignedToReference = false,
        ).toLyrics()

        assertEquals(LyricsProvenance.AiGenerated, lyrics.provenance)
    }

    @Test
    fun `a transcription shown only because lrclib had nothing is still labelled`() {
        // Never promoted, never chosen — displayed because it is all there is. The reader still needs
        // to know a machine wrote it, so the source-picking rule must match the server's.
        val lyrics = response(transcribedSynced = "[00:03.00]what the model heard").toLyrics()

        assertEquals(LyricsProvenance.AiGenerated, lyrics.provenance)
        assertEquals(true, lyrics.isTranscribed)
    }

    @Test
    fun `a transcription kept only for comparison does not relabel the lyrics on screen`() {
        val lyrics = response(
            synced = "[00:01.00]a line",
            plain = "a line",
            transcribedSynced = "[00:03.00]what the model heard",
        ).toLyrics()

        assertEquals(LyricsProvenance.Human, lyrics.provenance)
        assertEquals(false, lyrics.isTranscribed)
    }

    @Test
    fun `human lyrics re-timed by the probe are ai enhanced`() {
        val lyrics = response(synced = "[00:16.00]a line", lyricsSyncOffsetMs = 15000).toLyrics()

        assertEquals(LyricsProvenance.AiEnhanced, lyrics.provenance)
    }

    @Test
    fun `an unknown provenance string degrades to no claim rather than a wrong one`() {
        val lyrics = response(synced = "[00:01.00]a line", lyricsProvenance = "SomethingNew").toLyrics()

        assertEquals(LyricsProvenance.Human, lyrics.provenance)
    }
}
