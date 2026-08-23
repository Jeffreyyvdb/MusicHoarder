package com.musichoarder.app.player

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins the rule that decides when the music-video backdrop is hard-seeked back onto the song's
 * clock.
 *
 * The bug this guards is the one that froze the picture at 1.5x and 2x: a corrective seek pins the
 * clip's reported position at its target while it re-buffers, so a tolerance that a single UI tick
 * can blow turns one correction into an unbroken chain of them, and no frame ever reaches the
 * surface. The numbers below are that tick — 200 ms of song time per tick, times the rate.
 */
class VideoResyncTest {

    /** A tick that is not inside a cooldown and not waiting on a buffer. */
    private fun settled(driftMs: Long, rate: Float = 1f) =
        shouldResyncVideo(driftMs, rate, isBuffering = false, sinceLastResyncMs = 5_000)

    @Test
    fun `a clip inside the tolerance is left alone`() {
        assertFalse(settled(driftMs = 120))
        assertFalse(settled(driftMs = -120))
    }

    @Test
    fun `a clip past the tolerance is pulled back`() {
        assertTrue(settled(driftMs = 500))
        assertTrue("a clip running ahead drifts just as much", settled(driftMs = -500))
    }

    @Test
    fun `one tick of stall at 2x is not drift`() {
        // 200 ms of UI tick at 2x moves the song 400 ms. Under the old fixed 300 ms window that
        // alone triggered a seek, and the seek guaranteed the next tick would look worse still.
        assertFalse(settled(driftMs = 400, rate = 2f))
        assertFalse(settled(driftMs = 300, rate = 1.5f))
    }

    @Test
    fun `real drift at 2x is still corrected`() {
        assertTrue(settled(driftMs = 900, rate = 2f))
    }

    @Test
    fun `a buffering clip is waiting, not drifting`() {
        assertFalse(
            shouldResyncVideo(driftMs = 900, rate = 1f, isBuffering = true, sinceLastResyncMs = 5_000)
        )
    }

    @Test
    fun `a buffering clip on the wrong part of the song is still moved`() {
        // What a scrub of the audio leaves behind: not drift, dislocation.
        assertTrue(
            shouldResyncVideo(driftMs = 30_000, rate = 1f, isBuffering = true, sinceLastResyncMs = 5_000)
        )
    }

    @Test
    fun `no second correction lands inside the cooldown`() {
        assertFalse(shouldResyncVideo(900, rate = 1f, isBuffering = false, sinceLastResyncMs = 200))
        assertFalse(shouldResyncVideo(30_000, rate = 2f, isBuffering = true, sinceLastResyncMs = 999))
        assertTrue(shouldResyncVideo(900, rate = 1f, isBuffering = false, sinceLastResyncMs = 1_000))
    }

    @Test
    fun `the tolerance grows with the rate and never shrinks below it`() {
        assertEquals(300L, driftToleranceMs(1f))
        assertEquals(450L, driftToleranceMs(1.5f))
        assertEquals(600L, driftToleranceMs(2f))
        assertEquals("a slow song has room to spare — hold the tight window", 300L, driftToleranceMs(0.5f))
    }
}
