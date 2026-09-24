package com.musichoarder.app.data

import kotlin.math.pow

/**
 * Now Playing's legibility rule, ported from the web's `now-playing/cover-dim.ts` so both players
 * dim the same cover by the same amount.
 *
 * The player's text is white over a blurred wash of the cover, so a black layer of alpha `a` sits
 * between them, sized per cover: just enough to bring the BRIGHTEST part of the wash down to about
 * 70/255 grey. At that level white text clears 9.4:1, the 72% secondary tone 5.8:1 and the 55%
 * inactive lyric lines 4.2:1 — everywhere on the screen, not just on average.
 *
 *   a = clamp(0.35, 1 − 70/g, 0.75)   with g = the brightest cell of an 8×8 sample
 *
 * Not the cover's average grey, which the first cut used: a pale disc on a dark field averages
 * low, gets a light dim, and leaves the wash bright exactly where a lyric line lands (the web
 * measured 2.4:1 on a test cover). Brightness is measured as WCAG does — relative luminance, turned back into the
 * sRGB grey with the same luminance — because a saturated green is far brighter than its Rec. 601
 * luma says.
 *
 * A dark cover still gets the 0.35 floor, so the wash never looks like an unlit screen; a white
 * cover lands at ≈0.73, under the 0.75 ceiling, still reading as that cover. A cover that cannot
 * be measured gets the white cover's dim ([DIM_UNKNOWN]): with nothing known, only the worst case
 * keeps the promise.
 */
object CoverDim {
    const val DIM_TARGET_GREY = 70.0
    const val DIM_MIN = 0.35f
    const val DIM_MAX = 0.75f
    const val DIM_UNKNOWN = 0.73f
}

/** The dim alpha for a wash whose brightest sRGB-equivalent grey (0–255) is [grey]; null → unknown. */
fun dimAlphaForGrey(grey: Double?): Float {
    if (grey == null || !grey.isFinite()) return CoverDim.DIM_UNKNOWN
    if (grey <= 0.0) return CoverDim.DIM_MIN
    return (1.0 - CoverDim.DIM_TARGET_GREY / grey).toFloat().coerceIn(CoverDim.DIM_MIN, CoverDim.DIM_MAX)
}

private fun toLinear(channel: Double): Double {
    val c = channel / 255.0
    return if (c <= 0.04045) c / 12.92 else ((c + 0.055) / 1.055).pow(2.4)
}

private fun toSrgb(linear: Double): Double {
    val c = if (linear <= 0.0031308) linear * 12.92 else 1.055 * linear.pow(1 / 2.4) - 0.055
    return (c * 255.0).coerceIn(0.0, 255.0)
}

/** The sRGB grey (0–255) with the same WCAG relative luminance as this colour. */
fun equivalentGrey(r: Double, g: Double, b: Double): Double =
    toSrgb(0.2126 * toLinear(r) + 0.7152 * toLinear(g) + 0.0722 * toLinear(b))

/**
 * The brightest pixel of packed ARGB pixels (`Bitmap.getPixels`'s format), as an equivalent grey.
 * Each pixel of the 8×8 decode already stands for an eighth of the cover each way, as each cell
 * of the web's canvas does. Translucent pixels are composited over black, which is what sits under
 * the wash. Null for no pixels.
 */
fun brightestGrey(argb: IntArray): Double? {
    var max: Double? = null
    for (pixel in argb) {
        val alpha = ((pixel ushr 24) and 0xFF) / 255.0
        val grey = equivalentGrey(
            ((pixel shr 16) and 0xFF) * alpha,
            ((pixel shr 8) and 0xFF) * alpha,
            (pixel and 0xFF) * alpha,
        )
        if (max == null || grey > max) max = grey
    }
    return max
}
