package com.musichoarder.app.ui

import androidx.compose.animation.core.animateDpAsState
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Check
import androidx.compose.material.icons.rounded.FastForward
import androidx.compose.material.icons.rounded.FastRewind
import androidx.compose.material.icons.rounded.Pause
import androidx.compose.material.icons.rounded.PlayArrow
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Shadow
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.musichoarder.app.player.PlayerUiState
import com.musichoarder.app.ui.theme.MhTheme
import kotlin.math.roundToInt

/**
 * The web's transport, ported: `SongTransport.svelte` + `Scrubber.svelte`.
 *
 * A hairline scrubber over one row — `ghost · 0:10 · ⏪ ▶ ⏩ · 2:54 · 1×`. The glyphs are naked and
 * filled, with no disc and no hover wash (a translucent circle reads as a smudge on dark artwork);
 * the press feedback is a scale on the glyph itself. The ghost on the left is the same width as the
 * speed label on the right, which is what keeps the play button on the screen's centre line.
 */
@Composable
fun PlayerTransport(
    state: PlayerUiState,
    onPlayPause: () -> Unit,
    onNext: () -> Unit,
    onPrevious: () -> Unit,
    onSeek: (Long) -> Unit,
    onSetSpeed: (Float) -> Unit,
    modifier: Modifier = Modifier,
    /** The fullscreen-lyrics footer: scrubber and one big play button, no queue navigation. */
    minimal: Boolean = false,
    onSurface: Boolean = false,
) {
    val colors = MhTheme.colors

    // While a finger is on the bar the player's own position must not fight the drag.
    var scrubFraction by remember { mutableFloatStateOf(0f) }
    var isScrubbing by remember { mutableStateOf(false) }

    // Duration is unknown for the first moments of a track (and for some containers, longer). Left
    // to itself the bar would clamp the position into a 0..0 range and sit pinned at 100%, which
    // reads as a bug; show an inert bar and "--:--" until the real length arrives.
    val hasDuration = state.durationMs > 0
    val playedFraction = when {
        isScrubbing -> scrubFraction
        hasDuration -> (state.positionMs.toFloat() / state.durationMs).coerceIn(0f, 1f)
        else -> 0f
    }
    val shownPositionMs =
        if (isScrubbing && hasDuration) (scrubFraction * state.durationMs).toLong() else state.positionMs

    val timeStyle = legible(
        MaterialTheme.typography.bodySmall.copy(fontFeatureSettings = TABULAR_FIGURES),
        onSurface,
    )
    val timeColor = colors.mutedForeground

    Column(modifier = modifier.fillMaxWidth()) {
        MhScrubber(
            fraction = playedFraction,
            enabled = hasDuration,
            onScrub = { fraction ->
                isScrubbing = true
                scrubFraction = fraction
            },
            onScrubEnd = {
                onSeek((scrubFraction * state.durationMs).toLong())
                isScrubbing = false
            },
        )

        Spacer(Modifier.height(if (minimal) 4.dp else 6.dp))

        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(if (minimal) 4.dp else 12.dp),
        ) {
            // Mirrors the speed control on the right so the play glyph stays centred.
            Spacer(Modifier.width(SPEED_WIDTH))
            Text(
                text = formatDuration(shownPositionMs),
                style = timeStyle,
                color = timeColor,
                textAlign = TextAlign.End,
                modifier = Modifier.width(TIME_WIDTH),
            )

            Row(
                modifier = Modifier.weight(1f),
                horizontalArrangement = Arrangement.Center,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                if (!minimal) {
                    TransportGlyph(
                        icon = Icons.Rounded.FastRewind,
                        contentDescription = "Previous track",
                        hitSize = 36.dp,
                        glyphSize = 22.dp,
                        enabled = state.hasPrevious,
                        onClick = onPrevious,
                    )
                    Spacer(Modifier.width(8.dp))
                }
                PlayGlyph(
                    isPlaying = state.isPlaying,
                    isBuffering = state.isBuffering,
                    hitSize = if (minimal) 48.dp else 44.dp,
                    glyphSize = if (minimal) 32.dp else 28.dp,
                    onClick = onPlayPause,
                )
                if (!minimal) {
                    Spacer(Modifier.width(8.dp))
                    TransportGlyph(
                        icon = Icons.Rounded.FastForward,
                        contentDescription = "Next track",
                        hitSize = 36.dp,
                        glyphSize = 22.dp,
                        enabled = state.hasNext,
                        onClick = onNext,
                    )
                }
            }

            Text(
                text = if (hasDuration) formatDuration(state.durationMs) else "--:--",
                style = timeStyle,
                color = timeColor,
                modifier = Modifier.width(TIME_WIDTH),
            )
            PlayerSpeedControl(rate = state.playbackRate, onSetSpeed = onSetSpeed)
        }
    }
}

/**
 * The honest Apple-Music-style scrubber: a 3dp hairline capsule that thickens under a finger, a
 * `primary` fill, and no thumb. Tap anywhere to seek, drag to scrub — one gesture loop handles
 * both, since a tap is just a drag that never moved.
 */
@Composable
private fun MhScrubber(
    fraction: Float,
    enabled: Boolean,
    onScrub: (Float) -> Unit,
    onScrubEnd: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val colors = MhTheme.colors
    var isPressed by remember { mutableStateOf(false) }
    val trackHeight by animateDpAsState(
        targetValue = if (isPressed) 7.dp else 3.dp,
        animationSpec = tween(durationMillis = 150),
        label = "scrubber-height",
    )

    Box(
        modifier = modifier
            .fillMaxWidth()
            .height(16.dp)
            .pointerInput(enabled) {
                if (!enabled) return@pointerInput
                awaitEachGesture {
                    val down = awaitFirstDown(requireUnconsumed = false)
                    isPressed = true
                    onScrub((down.position.x / size.width).coerceIn(0f, 1f))
                    while (true) {
                        val change = awaitPointerEvent().changes.firstOrNull { it.id == down.id }
                        if (change == null || !change.pressed) break
                        onScrub((change.position.x / size.width).coerceIn(0f, 1f))
                        change.consume()
                    }
                    isPressed = false
                    onScrubEnd()
                }
            },
        contentAlignment = Alignment.Center,
    ) {
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .height(trackHeight)
                .clip(CircleShape)
                .background(colors.foreground.copy(alpha = 0.2f)),
        ) {
            Box(
                modifier = Modifier
                    .fillMaxWidth(fraction)
                    .fillMaxHeight()
                    .clip(CircleShape)
                    .background(if (enabled) colors.primary else colors.foreground.copy(alpha = 0.2f)),
            )
        }
    }
}

/** The quiet tabular speed label at the row's edge; muted at 1x, `primary` at anything else. */
@Composable
private fun PlayerSpeedControl(rate: Float, onSetSpeed: (Float) -> Unit) {
    val colors = MhTheme.colors
    var expanded by remember { mutableStateOf(false) }

    Box {
        Text(
            text = "${formatRate(rate)}×",
            style = MaterialTheme.typography.labelSmall.copy(fontFeatureSettings = TABULAR_FIGURES),
            fontWeight = FontWeight.Medium,
            color = if (isNormalRate(rate)) colors.mutedForeground.copy(alpha = 0.5f) else colors.primary,
            textAlign = TextAlign.End,
            modifier = Modifier
                .width(SPEED_WIDTH)
                .clip(CircleShape)
                .clickable { expanded = true }
                .padding(vertical = 6.dp),
        )
        DropdownMenu(expanded = expanded, onDismissRequest = { expanded = false }) {
            SPEED_OPTIONS.forEach { option ->
                DropdownMenuItem(
                    text = {
                        Text(
                            if (isNormalRate(option)) "Normal" else "${formatRate(option)}×",
                            style = MaterialTheme.typography.bodyMedium,
                            color = colors.foreground,
                        )
                    },
                    trailingIcon = {
                        if (formatRate(option) == formatRate(rate)) {
                            Icon(
                                Icons.Rounded.Check,
                                contentDescription = null,
                                tint = colors.mutedForeground,
                                modifier = Modifier.size(14.dp),
                            )
                        }
                    },
                    onClick = {
                        expanded = false
                        onSetSpeed(option)
                    },
                )
            }
        }
    }
}

@Composable
private fun PlayGlyph(
    isPlaying: Boolean,
    isBuffering: Boolean,
    hitSize: Dp,
    glyphSize: Dp,
    onClick: () -> Unit,
) {
    val colors = MhTheme.colors
    if (isBuffering && !isPlaying) {
        Box(modifier = Modifier.size(hitSize), contentAlignment = Alignment.Center) {
            CircularProgressIndicator(
                modifier = Modifier.size(glyphSize * 0.7f),
                strokeWidth = 2.5.dp,
                color = colors.foreground,
            )
        }
        return
    }
    TransportGlyph(
        icon = if (isPlaying) Icons.Rounded.Pause else Icons.Rounded.PlayArrow,
        contentDescription = if (isPlaying) "Pause" else "Play",
        hitSize = hitSize,
        glyphSize = glyphSize,
        onClick = onClick,
    )
}

@Composable
private fun TransportGlyph(
    icon: ImageVector,
    contentDescription: String,
    hitSize: Dp,
    glyphSize: Dp,
    enabled: Boolean = true,
    onClick: () -> Unit,
) {
    val colors = MhTheme.colors
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val scale by animateFloatAsState(
        targetValue = if (pressed) 0.9f else 1f,
        animationSpec = tween(durationMillis = 100),
        label = "transport-press",
    )
    Box(
        modifier = Modifier
            .size(hitSize)
            .scale(scale)
            // No indication: a ripple disc is exactly the chrome these bare glyphs are avoiding.
            .clickable(
                interactionSource = interaction,
                indication = null,
                enabled = enabled,
                onClick = onClick,
            ),
        contentAlignment = Alignment.Center,
    ) {
        Icon(
            icon,
            contentDescription = contentDescription,
            tint = if (enabled) colors.foreground else colors.foreground.copy(alpha = 0.3f),
            modifier = Modifier.size(glyphSize),
        )
    }
}

/**
 * Playback-speed presets, pitch-preserved. Taken verbatim from `SongTransport.svelte` — the slow
 * end is deliberately finer-grained than the fast end, because that is the half people use to sing
 * or play along.
 */
private val SPEED_OPTIONS = listOf(0.5f, 0.65f, 0.75f, 0.85f, 1f, 1.1f, 1.25f, 1.5f)

private const val TABULAR_FIGURES = "tnum"
private val TIME_WIDTH = 40.dp
private val SPEED_WIDTH = 32.dp

private fun isNormalRate(rate: Float) = (rate - 1f) in -0.001f..0.001f

/** `1×`, `1.25×`, `0.65×` — no trailing zero on a whole rate, matching the web's label. */
internal fun formatRate(rate: Float): String {
    val hundredths = (rate * 100).roundToInt()
    if (hundredths % 100 == 0) return (hundredths / 100).toString()
    return (hundredths / 100f).toString().trimEnd('0').trimEnd('.')
}

/**
 * The web paints the player's chrome with `text-shadow: 0 0 4px var(--background), 0 1px 14px
 * var(--background)` so labels stay readable when a bright video frame is playing behind them.
 * This is the same idea with the one shadow Compose gives a [TextStyle].
 *
 * [onSurface] is false on the plain page, where a shadow in the page colour would only muddy the
 * glyphs.
 */
@Composable
internal fun legible(base: TextStyle, onSurface: Boolean): TextStyle =
    if (!onSurface) base else base.copy(
        shadow = Shadow(
            color = MhTheme.colors.background,
            offset = Offset(0f, 2f),
            blurRadius = 18f,
        )
    )
