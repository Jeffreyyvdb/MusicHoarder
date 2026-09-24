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
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.FastForward
import androidx.compose.material.icons.rounded.FastRewind
import androidx.compose.material.icons.rounded.Pause
import androidx.compose.material.icons.rounded.PlayArrow
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Shadow
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.layout
import androidx.compose.ui.semantics.ProgressBarRangeInfo
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.disabled
import androidx.compose.ui.semantics.progressBarRangeInfo
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.setProgress
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.musichoarder.app.player.PlayerUiState
import com.musichoarder.app.ui.theme.LocalMhColors
import com.musichoarder.app.ui.theme.MhColors
import com.musichoarder.app.ui.theme.MhMenuShape
import com.musichoarder.app.ui.theme.MhTheme
import kotlin.math.roundToInt

/**
 * The web's transport, ported: `SongTransport.svelte` + `Scrubber.svelte`, in the iOS Now Playing
 * shape both clients now share.
 *
 * The scrubber, then the times UNDER it — elapsed at the leading end, time remaining (`−2:04`) at
 * the trailing end, and a speed capsule between them only while the song is not at 1× — then
 * previous / play-pause / next spread evenly across the width at 56 / 72 / 56dp. The glyphs are
 * naked and filled, with no disc and no ripple (a translucent circle reads as a smudge on dark
 * artwork); the press feedback is a scale on the glyph itself. The row is symmetric, which is what
 * keeps the play button on the screen's centre line.
 *
 * [menuColors] is the palette the speed menu opens in: the player's popups keep the plain dark
 * tokens on their solid surface rather than its white-on-cover media appearance.
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
    onSurface: Boolean = false,
    menuColors: MhColors? = null,
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
    val remainingMs = (state.durationMs - shownPositionMs).coerceAtLeast(0)

    val timeStyle = legible(
        MaterialTheme.typography.labelMedium.copy(fontFeatureSettings = TABULAR_FIGURES),
        onSurface,
    )

    Column(modifier = modifier.fillMaxWidth()) {
        MhScrubber(
            fraction = playedFraction,
            positionMs = shownPositionMs,
            durationMs = state.durationMs,
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

        Row(
            modifier = Modifier
                .fillMaxWidth()
                // The bar sits in the middle of its 48dp touch strip, which would leave the times
                // 22dp under it; tuck them up into the strip's lower half (the web's `mt-1.5`). The
                // strip still takes the touches there — plain text has no pointer input to steal it.
                .pullUp(TIMES_PULL_UP),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(
                text = formatDuration(shownPositionMs),
                style = timeStyle,
                color = colors.mutedForeground,
                modifier = Modifier.weight(1f),
            )
            // Only while it says something: at 1× the speed lives in the ⋮ menu, as on the web.
            if (!isNormalRate(state.playbackRate)) {
                PlayerSpeedCapsule(rate = state.playbackRate, onSetSpeed = onSetSpeed, menuColors = menuColors)
            }
            Text(
                // U+2212, the web's minus: a hyphen sits too high and too short next to the digits.
                text = if (hasDuration) "\u2212${formatDuration(remainingMs)}" else "--:--",
                style = timeStyle,
                color = colors.mutedForeground,
                textAlign = TextAlign.End,
                modifier = Modifier
                    .weight(1f)
                    .semantics {
                        contentDescription =
                            if (hasDuration) "${formatDuration(remainingMs)} remaining" else "Length unknown"
                    },
            )
        }

        Spacer(Modifier.height(8.dp))

        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceEvenly,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            TransportGlyph(
                icon = Icons.Rounded.FastRewind,
                contentDescription = "Previous track",
                hitSize = 56.dp,
                glyphSize = 36.dp,
                // Never off while a song is loaded: at the top of the queue Previous restarts it
                // (`seekToPrevious`), the rule both clients now share.
                enabled = state.isActive,
                onClick = onPrevious,
            )
            PlayGlyph(
                isPlaying = state.isPlaying,
                isBuffering = state.isBuffering,
                hitSize = 72.dp,
                glyphSize = 48.dp,
                onClick = onPlayPause,
            )
            TransportGlyph(
                icon = Icons.Rounded.FastForward,
                contentDescription = "Next track",
                hitSize = 56.dp,
                glyphSize = 36.dp,
                enabled = state.hasNext,
                onClick = onNext,
            )
        }
    }
}

/**
 * The honest Apple-Music-style scrubber: a 4dp capsule that grows to 8dp under a finger, filled in
 * the label colour, and no thumb — the finger is the thumb and the bar's growth is the feedback.
 * Apple fills progress with the text colour rather than the tint, which stays for things you tap;
 * the web's scrubber made the same move. Tap anywhere to seek, drag to scrub — one gesture loop
 * handles both, since a tap is just a drag that never moved.
 */
@Composable
private fun MhScrubber(
    fraction: Float,
    /** For [stateDescription] only — `onScrub`/`onScrubEnd` already work in [fraction]. */
    positionMs: Long,
    durationMs: Long,
    enabled: Boolean,
    onScrub: (Float) -> Unit,
    onScrubEnd: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val colors = MhTheme.colors
    var isPressed by remember { mutableStateOf(false) }
    val trackHeight by animateDpAsState(
        targetValue = if (isPressed) 8.dp else 4.dp,
        animationSpec = tween(durationMillis = 150),
        label = "scrubber-height",
    )

    // `pointerInput` keeps running the lambda it was given when its key last changed — a
    // recomposition alone does not replace it. The callbacks close over the track's duration, so
    // without this the gesture would go on seeking against whatever the duration was when the bar
    // first became scrubbable, and land on the wrong second of every track after the first.
    val currentOnScrub by rememberUpdatedState(onScrub)
    val currentOnScrubEnd by rememberUpdatedState(onScrubEnd)

    Box(
        modifier = modifier
            .fillMaxWidth()
            // Visually a 4-8dp capsule, but the drag target itself must clear Material's 48dp
            // touch minimum — the web's equivalent shipped at 16px and that was a Critical finding.
            .heightIn(min = 48.dp)
            // TalkBack gets no semantics at all without this — worse than the web's own gap (F09:
            // its hand-rolled sliders announce a bare percentage). Read a time, not a fraction, to
            // match the web's aria-valuetext fix; `setProgress` lets a two-finger swipe seek too.
            .semantics(mergeDescendants = true) {
                contentDescription = "Seek"
                if (enabled) {
                    progressBarRangeInfo = ProgressBarRangeInfo(current = fraction, range = 0f..1f)
                    stateDescription = "${formatDuration(positionMs)} of ${formatDuration(durationMs)}"
                    setProgress { target ->
                        val clamped = target.coerceIn(0f, 1f)
                        onScrub(clamped)
                        onScrubEnd()
                        true
                    }
                } else {
                    disabled()
                }
            }
            .pointerInput(enabled) {
                if (!enabled) return@pointerInput
                awaitEachGesture {
                    val down = awaitFirstDown(requireUnconsumed = false)
                    isPressed = true
                    currentOnScrub((down.position.x / size.width).coerceIn(0f, 1f))
                    while (true) {
                        val change = awaitPointerEvent().changes.firstOrNull { it.id == down.id }
                        if (change == null || !change.pressed) break
                        currentOnScrub((change.position.x / size.width).coerceIn(0f, 1f))
                        change.consume()
                    }
                    isPressed = false
                    currentOnScrubEnd()
                }
            },
        contentAlignment = Alignment.Center,
    ) {
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .height(trackHeight)
                .clip(CircleShape)
                // `bg-foreground/25` — white over the player's dimmed wash, the one surface this
                // bar sits on.
                .background(colors.foreground.copy(alpha = 0.25f)),
        ) {
            if (enabled) {
                Box(
                    modifier = Modifier
                        .fillMaxWidth(fraction)
                        .fillMaxHeight()
                        .clip(CircleShape)
                        .background(colors.foreground),
                )
            }
        }
    }
}

/**
 * The speed capsule between the times — only up while a non-1× speed is on, and a tap on it opens
 * the presets, so turning a slowed practice run back to normal is one tap from where it shows.
 * `bg-primary/15 text-primary` on the web, which inside the player is white.
 */
@Composable
private fun PlayerSpeedCapsule(rate: Float, onSetSpeed: (Float) -> Unit, menuColors: MhColors?) {
    val colors = MhTheme.colors
    var expanded by remember { mutableStateOf(false) }

    Box {
        Text(
            text = "${formatRate(rate)}×",
            style = MaterialTheme.typography.labelMedium.copy(fontFeatureSettings = TABULAR_FIGURES),
            fontWeight = FontWeight.SemiBold,
            color = colors.primary,
            modifier = Modifier
                .clip(CircleShape)
                .background(colors.primary.copy(alpha = 0.15f))
                // A 22dp capsule; Compose widens a small touch target to the 48dp minimum on its
                // own, so the hit area grows without the row between the times growing with it.
                .clickable(onClickLabel = "Change playback speed", role = Role.Button) { expanded = true }
                .semantics { contentDescription = "Playback speed, ${speedLabel(rate)}" }
                .padding(horizontal = 8.dp, vertical = 3.dp),
        )
        PlaybackSpeedMenu(
            expanded = expanded,
            rate = rate,
            onSetSpeed = onSetSpeed,
            onDismiss = { expanded = false },
            menuColors = menuColors ?: colors,
        )
    }
}

/** The presets as a menu of their own — the capsule's, anchored where it sits. */
@Composable
private fun PlaybackSpeedMenu(
    expanded: Boolean,
    rate: Float,
    onSetSpeed: (Float) -> Unit,
    onDismiss: () -> Unit,
    menuColors: MhColors,
) {
    CompositionLocalProvider(LocalMhColors provides menuColors) {
        DropdownMenu(
            expanded = expanded,
            onDismissRequest = onDismiss,
            shape = MhMenuShape,
            containerColor = menuColors.popover,
        ) {
            PlaybackSpeedItems(rate = rate, onSetSpeed = onSetSpeed, close = onDismiss)
        }
    }
}

/**
 * The eight presets as menu rows, "Normal" at 1× and a check on the current one — shared by the
 * capsule's menu and the player's ⋮ › Playback speed page, so the two cannot drift.
 */
@Composable
fun PlaybackSpeedItems(rate: Float, onSetSpeed: (Float) -> Unit, close: () -> Unit) {
    SPEED_OPTIONS.forEach { option ->
        MhMenuCheckItem(
            label = speedLabel(option),
            checked = formatRate(option) == formatRate(rate),
            onClick = {
                close()
                onSetSpeed(option)
            },
        )
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
        // Named, as the mini player's is: a bare spinner reads as nothing to TalkBack.
        Box(
            modifier = Modifier.size(hitSize).semantics { contentDescription = "Loading" },
            contentAlignment = Alignment.Center,
        ) {
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
private val TIMES_PULL_UP = 10.dp

/** Moves the content up by [by] and gives that much height back, so what follows moves up too. */
private fun Modifier.pullUp(by: Dp): Modifier = layout { measurable, constraints ->
    val placeable = measurable.measure(constraints)
    val pull = by.roundToPx()
    layout(placeable.width, (placeable.height - pull).coerceAtLeast(0)) { placeable.place(0, -pull) }
}

internal fun isNormalRate(rate: Float) = (rate - 1f) in -0.001f..0.001f

/** "Normal" at 1×, the menu's word for it on both clients; "1.25×" otherwise. */
internal fun speedLabel(rate: Float) = if (isNormalRate(rate)) "Normal" else "${formatRate(rate)}×"

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
