package com.musichoarder.app.data

import kotlin.math.pow
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Mirrors the web's `cover-dim.test.ts` case for case: the two players must dim the same cover by
 * the same amount, or the same song reads differently on the phone and in the browser.
 */
class CoverDimTest {

    // ---- dimAlphaForGrey ------------------------------------------------------------------------

    @Test
    fun `brings the brightest cell down to the target grey`() {
        // 1 − 70/140 = 0.5, and 140 × (1 − 0.5) = 70.
        assertEquals(0.5f, dimAlphaForGrey(140.0), 0.0001f)
    }

    @Test
    fun `keeps a floor for dark covers and a ceiling for white ones`() {
        assertEquals(CoverDim.DIM_MIN, dimAlphaForGrey(0.0))
        assertEquals(CoverDim.DIM_MIN, dimAlphaForGrey(60.0))
        // A white cover lands just under the cap: 1 − 70/255 ≈ 0.73.
        assertEquals(0.7255f, dimAlphaForGrey(255.0), 0.001f)
        assertTrue(dimAlphaForGrey(255.0) <= CoverDim.DIM_MAX)
    }

    @Test
    fun `assumes the worst when the cover could not be measured`() {
        assertEquals(CoverDim.DIM_UNKNOWN, dimAlphaForGrey(null))
        assertEquals(CoverDim.DIM_UNKNOWN, dimAlphaForGrey(Double.NaN))
        // The unknown dim holds even over white.
        assertTrue(255 * (1 - CoverDim.DIM_UNKNOWN) <= CoverDim.DIM_TARGET_GREY)
    }

    // ---- equivalentGrey -------------------------------------------------------------------------

    @Test
    fun `measures colour by luminance, not luma`() {
        assertEquals(255.0, equivalentGrey(255.0, 255.0, 255.0), 0.01)
        assertEquals(0.0, equivalentGrey(0.0, 0.0, 0.0), 0.01)
        assertEquals(128.0, equivalentGrey(128.0, 128.0, 128.0), 0.5)
        // Pure green is as bright as a ~220 grey (its Rec. 601 luma says 150).
        assertTrue(equivalentGrey(0.0, 255.0, 0.0) > 215)
        assertTrue(equivalentGrey(0.0, 0.0, 255.0) < 80)
    }

    // ---- brightestGrey --------------------------------------------------------------------------

    @Test
    fun `takes the brightest pixel, compositing translucency over black`() {
        val white = argb(255, 255, 255, 255)
        val black = argb(0, 0, 0, 255)
        val clearWhite = argb(255, 255, 255, 0)
        assertEquals(255.0, brightestGrey(intArrayOf(black, white))!!, 0.01)
        assertEquals(0.0, brightestGrey(intArrayOf(black, clearWhite))!!, 0.01)
        assertNull(brightestGrey(intArrayOf()))
    }

    // ---- the rule, end to end -------------------------------------------------------------------

    @Test
    fun `keeps every tone legible over the bright half of a half-white, half-black cover`() {
        val data = sample { x, _ -> if (x < 4) WHITE else BLACK }
        val a = dimAlphaForGrey(brightestGrey(data))
        val brightest = dimmed(WHITE, a)
        assertTrue(brightest[0] <= CoverDim.DIM_TARGET_GREY + 0.5)
        assertTrue(contrastOfWhite(1.0, brightest) >= 9)
        assertTrue(contrastOfWhite(0.72, brightest) >= 4.5)
        assertTrue(contrastOfWhite(0.55, brightest) >= 3)
    }

    @Test
    fun `dims for a pale disc even when the cover is dark on average`() {
        // A magenta field with a pale green disc in the middle: the average grey is low, the disc not.
        val magenta = doubleArrayOf(150.0, 20.0, 120.0)
        val paleGreen = doubleArrayOf(180.0, 235.0, 160.0)
        val data = sample { x, y ->
            if ((x - 3.5).pow(2) + (y - 3.5).pow(2) < 5) paleGreen else magenta
        }
        val a = dimAlphaForGrey(brightestGrey(data))
        for (cell in listOf(magenta, paleGreen)) {
            val bg = dimmed(cell, a)
            assertTrue(contrastOfWhite(0.72, bg) >= 4.5)
            assertTrue(contrastOfWhite(0.55, bg) >= 3)
        }
    }

    private companion object {
        val WHITE = doubleArrayOf(255.0, 255.0, 255.0)
        val BLACK = doubleArrayOf(0.0, 0.0, 0.0)

        fun argb(r: Int, g: Int, b: Int, a: Int): Int = (a shl 24) or (r shl 16) or (g shl 8) or b

        /** An 8×8 opaque sample filled from a function of the cell, packed as `getPixels` hands it. */
        fun sample(fill: (Double, Double) -> DoubleArray): IntArray = IntArray(64) { i ->
            val (r, g, b) = fill((i % 8).toDouble(), (i / 8).toDouble()).map { it.toInt() }
            argb(r, g, b, 255)
        }

        fun dimmed(rgb: DoubleArray, a: Float): DoubleArray = DoubleArray(3) { rgb[it] * (1 - a) }

        // WCAG contrast of white at `alpha` over an sRGB background, composited in sRGB as the
        // screen does.
        fun luminance(rgb: DoubleArray): Double {
            fun lin(c: Double): Double {
                val s = c / 255
                return if (s <= 0.04045) s / 12.92 else ((s + 0.055) / 1.055).pow(2.4)
            }
            return 0.2126 * lin(rgb[0]) + 0.7152 * lin(rgb[1]) + 0.0722 * lin(rgb[2])
        }

        fun contrastOfWhite(alpha: Double, bg: DoubleArray): Double {
            val fg = DoubleArray(3) { bg[it] + alpha * (255 - bg[it]) }
            val hi = maxOf(luminance(fg), luminance(bg))
            val lo = minOf(luminance(fg), luminance(bg))
            return (hi + 0.05) / (lo + 0.05)
        }
    }
}
