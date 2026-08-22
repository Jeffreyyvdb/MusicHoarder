package com.musichoarder.app.ui

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins the two fits the web player uses — `object-cover` for the ambient backdrop and
 * `object-contain` for the watch view — against the sizing that drives the `SurfaceView`.
 *
 * The bug this guards is the one the surface shipped with: no sizing at all, so every clip was
 * stretched to the phone's portrait box. The cases below are the shapes that actually occur — a
 * 16:9 music video on a tall phone, and a square or vertical clip.
 */
class VideoChildSizeTest {

    // A 1080x2424 phone, the shape of the screenshots.
    private val boxWidth = 1080
    private val boxHeight = 2424
    private val sixteenByNine = 16f / 9f

    @Test
    fun `crop fills a portrait box with a wide clip and overflows the width`() {
        val (width, height) = videoChildSize(boxWidth, boxHeight, sixteenByNine, crop = true)

        assertEquals("height must fill the box", boxHeight, height)
        assertTrue("width must overflow so nothing is letterboxed", width > boxWidth)
        assertEquals(sixteenByNine, width.toFloat() / height, 0.01f)
    }

    @Test
    fun `fit letterboxes a wide clip inside a portrait box`() {
        val (width, height) = videoChildSize(boxWidth, boxHeight, sixteenByNine, crop = false)

        assertEquals("width must fill the box", boxWidth, width)
        assertTrue("height must be short enough to leave bands", height < boxHeight)
        assertEquals(sixteenByNine, width.toFloat() / height, 0.01f)
    }

    @Test
    fun `fit pillarboxes a vertical clip inside a landscape box`() {
        val (width, height) = videoChildSize(1920, 1080, 9f / 16f, crop = false)

        assertEquals(1080, height)
        assertTrue(width < 1920)
        assertEquals(9f / 16f, width.toFloat() / height, 0.01f)
    }

    @Test
    fun `crop fills a landscape box with a vertical clip`() {
        val (width, height) = videoChildSize(1920, 1080, 9f / 16f, crop = true)

        assertEquals(1920, width)
        assertTrue(height > 1080)
    }

    @Test
    fun `a clip that already matches the box is left exactly alone in both modes`() {
        assertEquals(1080 to 1080, videoChildSize(1080, 1080, 1f, crop = true))
        assertEquals(1080 to 1080, videoChildSize(1080, 1080, 1f, crop = false))
    }

    @Test
    fun `an unknown ratio fills the box rather than collapsing it`() {
        assertEquals(boxWidth to boxHeight, videoChildSize(boxWidth, boxHeight, 0f, crop = true))
        assertEquals(boxWidth to boxHeight, videoChildSize(boxWidth, boxHeight, -1f, crop = false))
    }

    @Test
    fun `a box with no area yields no size`() {
        assertEquals(0 to boxHeight, videoChildSize(0, boxHeight, sixteenByNine, crop = true))
        assertEquals(boxWidth to 0, videoChildSize(boxWidth, 0, sixteenByNine, crop = false))
    }
}
