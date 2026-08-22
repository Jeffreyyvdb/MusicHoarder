package com.musichoarder.app.ui

import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.FrameLayout
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

/**
 * A real measure/layout pass over the frame that shapes the video surface.
 *
 * `VideoChildSizeTest` pins the arithmetic; this pins that the frame actually applies it — that the
 * child ends up at the computed size, centred, and (when cropping) hanging over the edges of a
 * parent whose `clipChildren` is what keeps the overflow off screen.
 */
@RunWith(AndroidJUnit4::class)
class VideoFrameLayoutTest {

    private val context = InstrumentationRegistry.getInstrumentation().targetContext

    private fun measured(ratio: Float, crop: Boolean, width: Int = 1080, height: Int = 2424): View {
        val frame = VideoFrameLayout(context).apply {
            aspectRatio = ratio
            this.crop = crop
            addView(
                View(context),
                FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT,
                    ViewGroup.LayoutParams.MATCH_PARENT,
                    Gravity.CENTER,
                ),
            )
        }
        frame.measure(
            View.MeasureSpec.makeMeasureSpec(width, View.MeasureSpec.EXACTLY),
            View.MeasureSpec.makeMeasureSpec(height, View.MeasureSpec.EXACTLY),
        )
        frame.layout(0, 0, width, height)
        assertEquals("the frame itself always fills its slot", width, frame.measuredWidth)
        assertEquals(height, frame.measuredHeight)
        return frame.getChildAt(0)
    }

    @Test
    fun cropOverflowsTheWidthSoNothingIsLetterboxed() {
        val child = measured(16f / 9f, crop = true)

        assertEquals(2424, child.measuredHeight)
        assertTrue("a 16:9 clip must overflow a portrait box", child.measuredWidth > 1080)
        // Centred, so the overflow is split evenly and the middle of the frame stays the middle.
        assertEquals(-(child.measuredWidth - 1080) / 2, child.left)
        assertNear("the right edge mirrors the left", 1080 + (child.measuredWidth - 1080) / 2, child.right)
    }

    @Test
    fun fitLetterboxesAndCentresTheClip() {
        val child = measured(16f / 9f, crop = false)

        assertEquals(1080, child.measuredWidth)
        assertEquals(608, child.measuredHeight)
        assertTrue("bands above and below", child.top > 0)
        assertNear("bands must be even", child.top, 2424 - child.bottom)
    }

    @Test
    fun anUnknownRatioStillFillsTheFrame() {
        val child = measured(0f, crop = false)

        assertEquals(1080, child.measuredWidth)
        assertEquals(2424, child.measuredHeight)
    }

    /** Rounding puts an odd overflow one pixel off dead-centre; that is not a failure. */
    private fun assertNear(message: String, expected: Int, actual: Int) {
        assertTrue("$message (expected $expected, was $actual)", kotlin.math.abs(expected - actual) <= 1)
    }
}
