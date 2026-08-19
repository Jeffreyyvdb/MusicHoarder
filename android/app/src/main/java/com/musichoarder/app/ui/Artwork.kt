package com.musichoarder.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.unit.dp
import coil3.compose.AsyncImage
import kotlin.math.cos
import kotlin.math.pow
import kotlin.math.sin

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
) {
    val tint = remember(artist, title) { albumTint(artist, title) }
    Box(
        modifier = modifier.clip(shape).background(tint),
        contentAlignment = Alignment.Center,
    ) {
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
