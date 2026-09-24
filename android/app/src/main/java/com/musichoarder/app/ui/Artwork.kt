package com.musichoarder.app.ui

import android.os.Build
import android.util.LruCache
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.BlurredEdgeTreatment
import androidx.compose.ui.draw.blur
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.draw.scale
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ColorFilter
import androidx.compose.ui.graphics.ColorMatrix
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import coil3.SingletonImageLoader
import coil3.compose.AsyncImage
import coil3.request.ImageRequest
import coil3.request.SuccessResult
import coil3.request.allowHardware
import coil3.toBitmap
import com.musichoarder.app.data.CoverDim
import com.musichoarder.app.data.brightestGrey
import com.musichoarder.app.data.dimAlphaForGrey
import kotlin.math.cos
import kotlin.math.pow
import kotlin.math.sin
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/**
 * Album art, with the web app's placeholder underneath it.
 *
 * The tile is not a fallback that only shows on failure — it is the ground the image fades in over,
 * so a list scrolling past unloaded covers looks like the web's grid of tinted squares rather than a
 * column of grey holes.
 */
@Composable
fun Artwork(
    url: String?,
    artist: String,
    title: String,
    modifier: Modifier = Modifier,
    shape: Shape = RoundedCornerShape(6.dp),
    fallbackUrl: String? = null,
) {
    val tint = remember(artist, title) { albumTint(artist, title) }
    Box(
        modifier = modifier.clip(shape).background(tint),
        contentAlignment = Alignment.Center,
    ) {
        // Both layers are simply stacked rather than wired through an error callback: an image that
        // fails paints nothing, so the one underneath shows through on its own. That is what the
        // artist grid needs, where the portrait endpoint 404s for anyone the providers do not know
        // and the album cover has to take over without a flash of empty tile.
        if (fallbackUrl != null) {
            AsyncImage(
                model = fallbackUrl,
                contentDescription = null,
                contentScale = ContentScale.Crop,
                modifier = Modifier.fillMaxSize(),
            )
        }
        if (url != null) {
            AsyncImage(
                model = url,
                contentDescription = null,
                contentScale = ContentScale.Crop,
                modifier = Modifier.fillMaxSize(),
            )
        }
    }
}

/**
 * Now Playing's ground — the web's media appearance, layer for layer: black, the cover blown up
 * to 150%, blurred hard and saturated so the wash carries the cover's colour rather than its
 * picture, then a black dim of [dimAlpha] (`rememberCoverDimAlpha`) that keeps white text legible
 * over whatever the cover is. The tint sits under the image, so a song without art still gets its
 * album's colour instead of a black room.
 *
 * `Modifier.blur` needs a `RenderEffect`, which is API 31 and up. Below that the other half of the
 * recipe does the work: the wash asks Coil for a tiny decode of the cover, and a 16 px image
 * stretched across a phone is already soft enough to read as a wash rather than a picture. Above
 * it the decode is still small, because the blur throws the detail away anyway.
 */
@Composable
fun AmbientBackdrop(
    url: String?,
    artist: String,
    title: String,
    dimAlpha: Float,
    modifier: Modifier = Modifier,
) {
    val tint = remember(artist, title) { albumTint(artist, title) }
    val context = LocalContext.current
    val request = remember(url, context) {
        url?.let { ImageRequest.Builder(context).data(it).size(WASH_SOURCE_PX).build() }
    }
    Box(modifier = modifier.clipToBounds().background(Color.Black)) {
        Box(
            modifier = Modifier
                .fillMaxSize()
                .scale(1.5f)
                .ambientBlur(64.dp)
                .background(tint)
        ) {
            if (request != null) {
                AsyncImage(
                    model = request,
                    contentDescription = null,
                    contentScale = ContentScale.Crop,
                    colorFilter = WashSaturation,
                    modifier = Modifier.fillMaxSize(),
                )
            }
        }
        Box(Modifier.fillMaxSize().background(Color.Black.copy(alpha = dimAlpha)))
    }
}

/** `saturate-150`, so the blurred wash reads as the cover's colour rather than a muddy grey. */
private val WashSaturation = ColorFilter.colorMatrix(ColorMatrix().apply { setToSaturation(1.5f) })

/** A soft source is the blur's job on API 31+, and the whole of it below. */
private val WASH_SOURCE_PX = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) 64 else 16

/**
 * How much black goes over this cover so the player's white text keeps its contrast — the web's
 * `coverDimAlpha`, sampled once per cover from an 8×8 decode (Coil's disk cache already holds the
 * image, so this costs a decode, not a download) and sized by its brightest cell, which is where a
 * lyric line can land. Anything that cannot be measured — no cover, a failed load — resolves to
 * [CoverDim.DIM_UNKNOWN], the white cover's dim, and a known cover answers from the cache straight
 * away, so reopening the player does not flash the default first.
 */
@Composable
fun rememberCoverDimAlpha(url: String?): Float {
    val context = LocalContext.current
    val measured by produceState(initialValue = url?.let(CoverDimCache::get) ?: CoverDim.DIM_UNKNOWN, url) {
        if (url == null) {
            value = CoverDim.DIM_UNKNOWN
            return@produceState
        }
        CoverDimCache.get(url)?.let {
            value = it
            return@produceState
        }
        val request = ImageRequest.Builder(context).data(url).size(8).allowHardware(false).build()
        val image = (SingletonImageLoader.get(context).execute(request) as? SuccessResult)?.image
        if (image == null) {
            // Not cached: a load that failed (offline, a server restarting) may well work next time.
            value = CoverDim.DIM_UNKNOWN
            return@produceState
        }
        val alpha = withContext(Dispatchers.Default) {
            // A cover that cannot be read back (a hardware bitmap that slipped through, a decoder
            // quirk) is simply an unmeasured one — the same answer as the web's tainted canvas.
            runCatching {
                val pixels = IntArray(64)
                image.toBitmap(8, 8).getPixels(pixels, 0, 8, 0, 0, 8, 8)
                dimAlphaForGrey(brightestGrey(pixels))
            }.getOrDefault(CoverDim.DIM_UNKNOWN)
        }
        CoverDimCache.put(url, alpha)
        value = alpha
    }
    return measured
}

/** Per cover URL, as on the web: a cover's grey does not change, so it is measured once. */
private val CoverDimCache = LruCache<String, Float>(64)

/** `blur-[64px]`, where the platform can do it at all. */
private fun Modifier.ambientBlur(radius: Dp): Modifier =
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
        blur(radius, BlurredEdgeTreatment.Unbounded)
    } else {
        this
    }

/**
 * Port of `frontend/src/lib/album-tint.ts`. Same (artist, title) tuple must yield the same tint on
 * both clients, so this reproduces the JS exactly — including `cyrb53`'s 32-bit multiplies and the
 * `>>>` operands being coerced to uint32 before shifting.
 */
fun albumTint(artist: String, title: String): Brush {
    val key = "${artist.trim().lowercase()}::${title.trim().lowercase()}"
    val hash = cyrb53(key)

    val hue = (hash % 360).toDouble()
    // JS `hash >>> n` runs ToUint32 on the operand first; `hash` can exceed 2^32, so mask before shifting.
    val u32 = hash and 0xFFFFFFFFL
    val t1 = ((u32 ushr 4) % 1000).toDouble() / 1000.0
    val t2 = ((u32 ushr 11) % 1000).toDouble() / 1000.0

    val chroma = lerp(0.10, 0.18, t1)
    val lightnessFrom = lerp(0.30, 0.45, t2)
    val lightnessTo = lerp(0.55, 0.70, t1)
    val hueTo = (hue + 40.0) % 360.0

    return Brush.linearGradient(
        colors = listOf(oklchToColor(lightnessFrom, chroma, hue), oklchToColor(lightnessTo, chroma, hueTo)),
        start = Offset.Zero,
        end = Offset.Infinite,
    )
}

private fun lerp(min: Double, max: Double, t: Double) = min + (max - min) * t

/** 32-bit string hash, matching the JS `cyrb53`. Kotlin `Int` math already wraps like `Math.imul`. */
internal fun cyrb53(input: String, seed: Int = 0): Long {
    var h1 = -0x21524111 xor seed // 0xdeadbeef
    var h2 = 0x41c6ce57 xor seed
    for (ch in input) {
        val c = ch.code
        h1 = (h1 xor c) * -0x61c8864f // 2654435761
        h2 = (h2 xor c) * 0x5f356495 // 1597334677
    }
    h1 = ((h1 xor (h1 ushr 16)) * -0x7a143595) xor ((h2 xor (h2 ushr 13)) * -0x3d4d51cb)
    h2 = ((h2 xor (h2 ushr 16)) * -0x7a143595) xor ((h1 xor (h1 ushr 13)) * -0x3d4d51cb)
    return 4294967296L * (2097151L and h2.toLong()) + (h1.toLong() and 0xFFFFFFFFL)
}

/** OKLCH → sRGB, so the palette and the tints can stay authored in the same space as the CSS. */
fun oklchToColor(l: Double, c: Double, hueDegrees: Double): Color {
    val h = Math.toRadians(hueDegrees)
    val a = c * cos(h)
    val b = c * sin(h)

    val lp = (l + 0.3963377774 * a + 0.2158037573 * b).pow(3)
    val mp = (l - 0.1055613458 * a - 0.0638541728 * b).pow(3)
    val sp = (l - 0.0894841775 * a - 1.2914855480 * b).pow(3)

    val r = 4.0767416621 * lp - 3.3077115913 * mp + 0.2309699292 * sp
    val g = -1.2684380046 * lp + 2.6097574011 * mp - 0.3413193965 * sp
    val bl = -0.0041960863 * lp - 0.7034186147 * mp + 1.7076147010 * sp

    return Color(encodeSrgb(r), encodeSrgb(g), encodeSrgb(bl))
}

private fun encodeSrgb(channel: Double): Float {
    val v = channel.coerceIn(0.0, 1.0)
    val encoded = if (v <= 0.0031308) 12.92 * v else 1.055 * v.pow(1.0 / 2.4) - 0.055
    return encoded.coerceIn(0.0, 1.0).toFloat()
}

/**
 * A list's or an album's total playing time — "4 h 21 min", "52 min", "48 sec". A port of the
 * web's `formatTotalDuration`, word for word, since the two clients print it under the same title.
 */
fun formatTotalDuration(seconds: Long): String {
    if (seconds <= 0) return "—"
    // The web's one duration style: minutes are the finest unit once there is a minute to show
    // ("21 min", never "20 min 56 sec"), rounded to the nearest.
    if (seconds < 60) return "$seconds sec"
    val totalMinutes = (seconds + 30) / 60
    val hours = totalMinutes / 60
    val minutes = totalMinutes % 60
    return when {
        hours > 0 -> if (minutes > 0) "$hours h $minutes min" else "$hours h"
        else -> "$minutes min"
    }
}

/** Formats a media position as `m:ss` (or `h:mm:ss` for the rare long track). */
fun formatDuration(milliseconds: Long): String {
    if (milliseconds <= 0) return "0:00"
    val totalSeconds = milliseconds / 1000
    val hours = totalSeconds / 3600
    val minutes = (totalSeconds % 3600) / 60
    val seconds = totalSeconds % 60
    return if (hours > 0) {
        "%d:%02d:%02d".format(hours, minutes, seconds)
    } else {
        "%d:%02d".format(minutes, seconds)
    }
}
