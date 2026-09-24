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

    // ── Baked-in bars ─────────────────────────────────────────────────────────
    // The web's crop-matte.test.ts cases, case for case: a 402 x 812 iPhone, a 16:9 upload, and a
    // 2.39:1 film mastered into it (21 rows of the server's 160-row grid top and bottom).
    private val letterbox = 21f / 160f

    @Test
    fun `crop grows a letterboxed clip until its picture fills a phone top to bottom`() {
        val (width, height) = videoChildSize(402, 812, sixteenByNine, crop = true, letterbox = letterbox)

        // The web scales the object-cover frame by 1.356: 1080 x (812 / 1080) x 1.356 = 1101.
        assertEquals(1957 to 1101, width to height)
        assertEquals("the picture spans the box's height", 812f, height * (1 - 2 * letterbox), 1f)
    }

    @Test
    fun `crop leaves a pillarbox alone on a portrait phone, where the bars are already off screen`() {
        assertEquals(
            videoChildSize(402, 812, sixteenByNine, crop = true),
            videoChildSize(402, 812, sixteenByNine, crop = true, pillarbox = 0.125f),
        )
    }

    @Test
    fun `crop pushes a pillarbox off a wide screen`() {
        // A 4:3 clip in 16:9 behind a 1440 x 900 box; the web's scale is 1.2 on a 1500-wide cover.
        assertEquals(1920 to 1080, videoChildSize(1440, 900, sixteenByNine, crop = true, pillarbox = 0.125f))
    }

    @Test
    fun `crop covers whichever pair of bars needs more when both are baked in`() {
        val both = videoChildSize(1440, 900, sixteenByNine, crop = true, letterbox = letterbox, pillarbox = 0.125f)
        val letterboxOnly = videoChildSize(1440, 900, sixteenByNine, crop = true, letterbox = letterbox)
        val pillarboxOnly = videoChildSize(1440, 900, sixteenByNine, crop = true, pillarbox = 0.125f)

        assertEquals(maxOf(letterboxOnly.first, pillarboxOnly.first), both.first)
    }

    @Test
    fun `an unmeasured or full-frame clip crops exactly as before`() {
        val plain = videoChildSize(boxWidth, boxHeight, sixteenByNine, crop = true)
        assertEquals(plain, videoChildSize(boxWidth, boxHeight, sixteenByNine, crop = true, letterbox = 0f, pillarbox = 0f))
    }

    @Test
    fun `a bar pair that would leave no picture is ignored`() {
        val plain = videoChildSize(402, 812, sixteenByNine, crop = true)
        assertEquals(plain, videoChildSize(402, 812, sixteenByNine, crop = true, letterbox = 0.5f))
        assertEquals(plain, videoChildSize(402, 812, sixteenByNine, crop = true, letterbox = -0.1f))
        assertEquals(plain, videoChildSize(402, 812, sixteenByNine, crop = true, letterbox = Float.NaN))
    }

    @Test
    fun `fit shows the frame as mastered, bars and all`() {
        assertEquals(
            videoChildSize(boxWidth, boxHeight, sixteenByNine, crop = false),
            videoChildSize(boxWidth, boxHeight, sixteenByNine, crop = false, letterbox = letterbox),
        )
    }

    @Test
    fun `a box with no area yields no size`() {
        assertEquals(0 to boxHeight, videoChildSize(0, boxHeight, sixteenByNine, crop = true))
        assertEquals(boxWidth to 0, videoChildSize(boxWidth, 0, sixteenByNine, crop = false))
    }
}
