package com.musichoarder.app.player

import androidx.annotation.OptIn
import androidx.media3.common.util.UnstableApi
import androidx.media3.extractor.Extractor
import androidx.media3.extractor.PositionHolder
import androidx.media3.extractor.SeekMap
import androidx.media3.extractor.mp3.Mp3Extractor
import androidx.media3.test.utils.FakeExtractorInput
import androidx.media3.test.utils.FakeExtractorOutput
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/**
 * The root cause of the player's dead scrubber, reproduced.
 *
 * A song only accepts a seek if its [SeekMap] says so: an unseekable one drops
 * `COMMAND_SEEK_IN_CURRENT_MEDIA_ITEM` from the session's available commands, and
 * `MediaController.seekTo` then *silently returns* — which is why the scrubber and the lyric lines
 * moved nothing and logged nothing. So the fix is proved here, at the extractor, where the decision
 * is actually made.
 *
 * The subject is the file the library is full of and the one this went wrong on: a constant-bitrate
 * MP3 whose encoder wrote no Xing/VBRI header, streamed with no `Content-Length` — the frontend
 * proxy used to strip it, so ExoPlayer could not fall back on the file size either.
 *
 * [CBR_SEEKING] is what `MediaSources.extractorsFactory()`'s two `setConstantBitrateSeeking*` calls
 * amount to for MP3 — the factory sets exactly these flags on the extractor it builds. The wiring
 * itself is two lines of straight-line code; what needed proving is that the flags change the
 * outcome for this file at all, which is what these cases pin.
 */
@OptIn(UnstableApi::class)
@RunWith(RobolectricTestRunner::class)
// Media3's `ParsableByteArray` reads `Build.FINGERPRINT` on every extractor read, so this needs a
// real Android runtime rather than the stubbed android.jar. Pinned below `compileSdk` because
// Robolectric ships images for the levels it supports, and nothing here is SDK-sensitive.
@Config(sdk = [34])
class Mp3SeekabilityTest {

    @Test
    fun `media3's defaults cannot seek a plain CBR mp3 of unknown length`() {
        // The shipped bug. Nothing is broken in Media3 here — with no seek table and no length there
        // is genuinely nothing to seek by until it is told to estimate.
        assertFalse(seekMap(flags = 0, knownLength = false).isSeekable)
    }

    @Test
    fun `constant bitrate seeking can`() {
        assertTrue(seekMap(CBR_SEEKING, knownLength = false).isSeekable)
    }

    @Test
    fun `a known length is enough on its own, which is why this hid`() {
        // Restoring `Content-Length` in the proxy fixes the same file without the flags. The two
        // halves of the fix are belt and braces, and this is the brace.
        assertTrue(seekMap(flags = 0, knownLength = true).isSeekable)
        assertTrue(seekMap(CBR_SEEKING, knownLength = true).isSeekable)
    }

    @Test
    fun `an estimated seek lands where the bitrate says it should`() {
        val points = seekMap(CBR_SEEKING, knownLength = true).getSeekPoints(500_000L)

        // 128 kbps is 16000 bytes of audio a second, so half a second in is ~8000 bytes. The landing
        // is snapped to a frame boundary, hence a window rather than an equality.
        val position = points.first.position
        assertTrue("expected a seek near 8000 bytes, got $position", position in 7_000..9_000)
    }

    /**
     * Runs an [Mp3Extractor] built with [flags] over [cbrMp3] and returns the seek map it publishes.
     *
     * [knownLength] is the `Content-Length` question: false makes the input report
     * [androidx.media3.common.C.LENGTH_UNSET], the way a stream through the old proxy did.
     */
    private fun seekMap(flags: Int, knownLength: Boolean): SeekMap {
        val extractor = Mp3Extractor(flags)
        val output = FakeExtractorOutput()
        extractor.init(output)

        val input = FakeExtractorInput.Builder()
            .setData(cbrMp3())
            .setSimulateUnknownLength(!knownLength)
            .build()
        val positionHolder = PositionHolder()

        // The seek map is published on the way to the first sample, so read until it appears.
        // `FakeExtractorOutput.seekMap` is unannotated and so arrives in Kotlin as non-null, which
        // it is not until the extractor publishes one — hence the explicitly nullable local.
        var published: SeekMap? = null
        var reads = 0
        while (published == null && reads++ < 10_000) {
            when (extractor.read(input, positionHolder)) {
                Extractor.RESULT_END_OF_INPUT -> break
                Extractor.RESULT_SEEK -> input.setPosition(positionHolder.position.toInt())
            }
            published = output.seekMap
        }
        return published ?: throw AssertionError("the extractor never published a seek map")
    }

    /**
     * A synthetic MPEG-1 Layer III stream: [frames] frames of 128 kbps / 44.1 kHz / joint stereo,
     * headers only with silence behind them.
     *
     * Built rather than committed because the *absence* of things is the whole point — no Xing or
     * Info tag in the first frame, no VBRI, nothing but a run of identical frame headers, which is
     * exactly what leaves an extractor with no way to seek. A binary fixture would hide that.
     */
    private fun cbrMp3(frames: Int = 40): ByteArray {
        val data = ByteArray(frames * FRAME_BYTES)
        for (frame in 0 until frames) {
            val at = frame * FRAME_BYTES
            data[at] = 0xFF.toByte()       // sync
            data[at + 1] = 0xFB.toByte()   // sync, MPEG-1, Layer III, no CRC
            data[at + 2] = 0x90.toByte()   // 128 kbps, 44.1 kHz, no padding
            data[at + 3] = 0x44.toByte()   // joint stereo, original
        }
        return data
    }

    private companion object {
        const val CBR_SEEKING = Mp3Extractor.FLAG_ENABLE_CONSTANT_BITRATE_SEEKING or
            Mp3Extractor.FLAG_ENABLE_CONSTANT_BITRATE_SEEKING_ALWAYS

        /** 144 * 128000 / 44100, unpadded. */
        const val FRAME_BYTES = 417
    }
}
