package com.musichoarder.app.data

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/** `GET /api/tracks/{id}/lyrics`. Everything is nullable — most songs have only some of it. */
@Serializable
data class LyricsResponse(
    val id: Int = 0,
    val lyricsStatus: String? = null,
    val isInstrumental: Boolean? = null,
    val synced: String? = null,
    val plain: String? = null,
    val transcribedSynced: String? = null,
    val transcribedPlain: String? = null,
    val transcriptionStatus: String? = null,
    /** "Lrclib" | "Transcribed" — which pair the viewer should show when both exist. */
    val preferredLyricsSource: String? = null,
)

/** What the lyrics view actually renders, after choosing a source and parsing. */
data class Lyrics(
    val lines: List<LrcLine>,
    val plainText: String?,
    val isInstrumental: Boolean,
    /** True when the shown text came from the AI transcription rather than LRCLIB. */
    val isTranscribed: Boolean,
) {
    val isSynced: Boolean get() = lines.isNotEmpty()
    val isEmpty: Boolean get() = lines.isEmpty() && plainText.isNullOrBlank()
}

data class LrcLine(val timeMs: Long, val text: String)

/**
 * Picks the source the web viewer would pick: the transcription when the song is flagged to prefer
 * it and has one, otherwise the LRCLIB pair. Synced text wins over plain; plain is kept as the
 * fallback for lyrics whose timestamps do not parse.
 */
fun LyricsResponse.toLyrics(): Lyrics {
    val preferTranscribed = preferredLyricsSource.equals("Transcribed", ignoreCase = true) &&
        !(transcribedSynced.isNullOrBlank() && transcribedPlain.isNullOrBlank())

    val syncedText = if (preferTranscribed) transcribedSynced ?: synced else synced ?: transcribedSynced
    val plainText = if (preferTranscribed) transcribedPlain ?: plain else plain ?: transcribedPlain

    return Lyrics(
        lines = syncedText?.let(::parseLrc).orEmpty(),
        // A synced document that has no parseable timestamps still has words worth showing.
        plainText = plainText?.takeIf { it.isNotBlank() } ?: syncedText?.takeIf { it.isNotBlank() },
        isInstrumental = isInstrumental == true,
        isTranscribed = preferTranscribed,
    )
}

/**
 * Matches one LRC timestamp tag: `[mm:ss]`, `[mm:ss.xx]` or `[mm:ss:xx]` (LRCLIB and some taggers
 * use a colon before the fractional part). Minutes may run past 99 on long tracks, so 1–3 digits.
 */
private val TIMESTAMP = Regex("""\[(\d{1,3}):([0-5]?\d)(?:[.:](\d{1,3}))?]""")

/**
 * Parses LRC-format synced lyrics into time-ordered lines — a port of
 * `frontend/src/lib/lyrics/parse-lrc.ts`, tolerances included.
 *
 * Handles CRLF input, `.`- or `:`-separated fractions, 1–3 digit minutes, and multiple timestamps
 * on one line (LRC repeats a lyric by tagging it several times). Metadata-only tags such as
 * `[ar:Artist]` carry no `mm:ss` timestamp and are skipped. Returns an empty list when nothing
 * parses — callers must treat that as "not synced" and fall back to the plain text rather than
 * rendering nothing.
 */
fun parseLrc(lrc: String): List<LrcLine> {
    val lines = mutableListOf<LrcLine>()
    for (raw in lrc.split(Regex("\r?\n"))) {
        val stamps = mutableListOf<Long>()
        var lastTagEnd = 0
        for (match in TIMESTAMP.findAll(raw)) {
            val minutes = match.groupValues[1].toLong()
            val seconds = match.groupValues[2].toLong()
            // Normalise the fraction to milliseconds: ".5" → 500ms, ".50" → 500ms, ".500" → 500ms.
            val fraction = match.groupValues[3].takeIf { it.isNotEmpty() }?.padEnd(3, '0')?.toLong() ?: 0L
            stamps += minutes * 60_000 + seconds * 1_000 + fraction
            lastTagEnd = match.range.last + 1
        }
        if (stamps.isEmpty()) continue
        val text = raw.substring(lastTagEnd).trim()
        for (timeMs in stamps) lines += LrcLine(timeMs, text)
    }
    return lines.sortedBy { it.timeMs }
}

/** `GET /songs/{id}/video` — 404 means no video is attached, which is the common case. */
@Serializable
data class VideoInfo(
    val status: String = "",
    /** videoTime = audioTime + syncOffsetMs / 1000 (positive = the video has an intro). */
    val syncOffsetMs: Int = 0,
    val syncSource: String = "",
    val syncConfidence: Double? = null,
    val durationSeconds: Int? = null,
    @SerialName("youTubeVideoId") val youTubeVideoId: String? = null,
    val lastError: String? = null,
    /** A Ready row whose mp4 vanished from disk — the stream would 404, so do not try. */
    val fileMissing: Boolean = false,
) {
    val isPlayable: Boolean get() = status.equals("Ready", ignoreCase = true) && !fileMissing
}
