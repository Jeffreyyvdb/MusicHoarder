package com.musichoarder.app.data

import kotlinx.serialization.json.JsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * The Kotlin port has to agree with `frontend/src/lib/lyrics/parse-lrc.test.ts` case for case — the
 * two clients read the same LRC documents, so a tolerance that exists on one side and not the other
 * shows up as a blank lyrics panel on exactly one of them.
 */
class ParseLrcTest {

    @Test
    fun `parses standard mm ss xx lines into time-ordered entries`() {
        val lrc = "[00:12.00]First line\n[00:17.20]Second line"
        assertEquals(
            listOf(LrcLine(12_000, "First line"), LrcLine(17_200, "Second line")),
            parseLrc(lrc),
        )
    }

    @Test
    fun `handles CRLF line endings`() {
        val lrc = "[00:01.00]One\r\n[00:02.00]Two"
        assertEquals(listOf(LrcLine(1_000, "One"), LrcLine(2_000, "Two")), parseLrc(lrc))
    }

    @Test
    fun `returns empty for plain text so callers can fall back`() {
        assertEquals(emptyList<LrcLine>(), parseLrc("Just some lyrics\nwith no timestamps"))
    }

    @Test
    fun `emits one entry per inline timestamp when a line repeats`() {
        assertEquals(
            listOf(LrcLine(12_000, "Chorus"), LrcLine(15_500, "Chorus")),
            parseLrc("[00:12.00][00:15.50]Chorus"),
        )
    }

    @Test
    fun `skips metadata tags that carry no timestamp`() {
        val lrc = "[ar:Artist]\n[ti:Title]\n[00:05.00]Real line"
        assertEquals(listOf(LrcLine(5_000, "Real line")), parseLrc(lrc))
    }

    @Test
    fun `accepts a colon-separated fractional part`() {
        assertEquals(listOf(LrcLine(62_500, "Text")), parseLrc("[01:02:50]Text"))
    }

    @Test
    fun `tolerates 1- and 3-digit minute and fractional fields`() {
        assertEquals(
            listOf(LrcLine(184_500, "short"), LrcLine(7_384_500, "long")),
            parseLrc("[3:04.5]short\n[123:04.500]long"),
        )
    }

    @Test
    fun `keeps an empty lyric line`() {
        assertEquals(listOf(LrcLine(10_000, "")), parseLrc("[00:10.00]"))
    }

    @Test
    fun `sorts out-of-order timestamps`() {
        assertEquals(
            listOf(LrcLine(5_000, "earlier"), LrcLine(20_000, "later")),
            parseLrc("[00:20.00]later\n[00:05.00]earlier"),
        )
    }

    @Test
    fun `prefers the transcription only when the song asks for it`() {
        val both = LyricsResponse(
            synced = "[00:01.00]lrclib",
            transcribedSynced = "[00:01.00]whisper",
            preferredLyricsSource = "Transcribed",
        )
        assertEquals("whisper", both.toLyrics().lines.single().text)
        assertEquals(true, both.toLyrics().isTranscribed)

        val lrclib = both.copy(preferredLyricsSource = "Lrclib")
        assertEquals("lrclib", lrclib.toLyrics().lines.single().text)
        assertEquals(false, lrclib.toLyrics().isTranscribed)
    }

    @Test
    fun `falls back to the synced text when only untimed lyrics parse to nothing`() {
        val untimed = LyricsResponse(synced = "no timestamps here")
        val lyrics = untimed.toLyrics()
        assertEquals(false, lyrics.isSynced)
        assertEquals("no timestamps here", lyrics.plainText)
    }

    @Test
    fun `only songs built into the destination library count as library rows`() {
        fun song(destination: String?, status: Int?) = ApiSong(
            id = 1,
            destinationPath = destination,
            libraryBuildStatus = status?.let { JsonPrimitive(it) },
        )

        // Done (3) with a destination path — the only combination the web lists.
        assertEquals(true, song("/dest/a.flac", 3).isBuilt)
        // Pending/Copied/Tagged, or no destination at all: pipeline state, not library.
        assertEquals(false, song("/dest/a.flac", 0).isBuilt)
        assertEquals(false, song("/dest/a.flac", 2).isBuilt)
        assertEquals(false, song(null, 3).isBuilt)
        assertEquals(false, song("", 3).isBuilt)
        assertEquals(false, song("/dest/a.flac", null).isBuilt)
        // The web's type allows the enum name, so accept it even though /songs sends a number.
        assertEquals(true, ApiSong(id = 1, destinationPath = "/d", libraryBuildStatus = JsonPrimitive("Done")).isBuilt)
        assertEquals(false, ApiSong(id = 1, destinationPath = "/d", libraryBuildStatus = JsonPrimitive("Tagged")).isBuilt)
    }
}
