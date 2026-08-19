package com.musichoarder.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.MusicNote
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import coil3.compose.AsyncImage
import kotlin.math.abs

/**
 * Album art with a graceful nothing-to-show state: a tinted tile carrying the first letter, so an
 * artless library still reads as a grid of distinct records rather than identical grey squares.
 */
@Composable
fun Artwork(
    url: String?,
    seed: String,
    modifier: Modifier = Modifier,
    shape: Shape = RoundedCornerShape(8.dp),
    letterSize: TextUnit = 20.sp,
) {
    Box(modifier = modifier.clip(shape).background(tintFor(seed)), contentAlignment = Alignment.Center) {
        if (url != null) {
            AsyncImage(
                model = url,
                contentDescription = null,
                contentScale = ContentScale.Crop,
                modifier = Modifier.fillMaxSize(),
            )
        } else {
            val letter = seed.trim().firstOrNull()?.uppercaseChar()
            if (letter != null && letter.isLetterOrDigit()) {
                Text(
                    text = letter.toString(),
                    style = MaterialTheme.typography.titleLarge.copy(
                        fontSize = letterSize,
                        fontWeight = FontWeight.SemiBold,
                    ),
                    color = Color.White.copy(alpha = 0.9f),
                )
            } else {
                Icon(
                    Icons.Rounded.MusicNote,
                    contentDescription = null,
                    tint = Color.White.copy(alpha = 0.9f),
                )
            }
        }
    }
}

/**
 * Deterministic tint per album/artist name — the same record always gets the same colour, which is
 * what makes the fallback tiles usable as a memory aid.
 */
private fun tintFor(seed: String): Brush {
    val hue = (abs(seed.lowercase().hashCode()) % 360).toFloat()
    val top = Color.hsl(hue, 0.45f, 0.42f)
    val bottom = Color.hsl((hue + 28f) % 360f, 0.42f, 0.28f)
    return Brush.verticalGradient(listOf(top, bottom))
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
