package com.musichoarder.app.ui

import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.StartOffset
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Check
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
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
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.takeOrElse
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.disabled
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.isTraversalGroup
import androidx.compose.ui.semantics.onClick
import androidx.compose.ui.semantics.role
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.toggleableState
import androidx.compose.ui.state.ToggleableState
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.musichoarder.app.ui.theme.MhMenuShape
import com.musichoarder.app.ui.theme.MhTheme

/**
 * The widgets the four library pages add to the shell's chrome: the artist index, the header's
 * sort / filter menu and the row badges.
 *
 * Kept beside [MhLargeHeader] and friends in `Chrome.kt` for the same reason: it should be obvious
 * when a page invents its own variant instead of reusing the shell's.
 */

/**
 * The artist index on the trailing edge — the web's phone index: A–Z then `#` down a 48dp-wide
 * column (the web's 44pt), and a tap or a drag down it JUMPS the grid to that letter rather than
 * filtering it. It replaced a three-row block of 28dp letter keys above the grid: under Material's
 * 48dp floor, and 90dp of header between the search field and the first portrait.
 *
 * Letters nobody falls under stay in the column, dimmed and inert, so it always reads as the whole
 * alphabet. Each letter is its own button to TalkBack ("Jump to M"); a finger scrubs the column the
 * way the iOS and Contacts indexes do.
 */
@Composable
fun MhLetterIndex(
    present: Set<String>,
    onJump: (String) -> Unit,
    modifier: Modifier = Modifier,
) {
    val colors = MhTheme.colors
    val density = LocalDensity.current
    var columnHeight by remember { mutableFloatStateOf(0f) }
    // 16dp a letter, shrinking evenly on a short screen so the whole run always fits.
    val letterPx = if (columnHeight <= 0f) with(density) { INDEX_LETTER_HEIGHT.toPx() }
    else minOf(with(density) { INDEX_LETTER_HEIGHT.toPx() }, columnHeight / INDEX_LETTERS.size)
    val letterHeight = with(density) { letterPx.toDp() }
    var lastJump by remember { mutableStateOf<String?>(null) }
    fun letterAt(y: Float): String? {
        // The letters are centred in the column, so find the run's top first.
        val top = (columnHeight - letterPx * INDEX_LETTERS.size) / 2f
        val i = ((y - top) / letterPx).toInt()
        return INDEX_LETTERS.getOrNull(i)?.takeIf { y >= top && it in present }
    }
    Column(
        modifier = modifier
            .width(48.dp)
            .fillMaxHeight()
            .onSizeChanged { columnHeight = it.height.toFloat() }
            .pointerInput(present) {
                awaitEachGesture {
                    val down = awaitFirstDown()
                    lastJump = null
                    letterAt(down.position.y)?.let { lastJump = it; onJump(it) }
                    do {
                        val event = awaitPointerEvent()
                        val change = event.changes.firstOrNull() ?: break
                        letterAt(change.position.y)?.let { letter ->
                            if (letter != lastJump) {
                                lastJump = letter
                                onJump(letter)
                            }
                        }
                        change.consume()
                    } while (event.changes.any { it.pressed })
                }
            }
            .semantics { isTraversalGroup = true },
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        for (letter in INDEX_LETTERS) {
            val enabled = letter in present
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .height(letterHeight)
                    .semantics {
                        role = Role.Button
                        contentDescription = if (letter == OTHER_NAMES) "Jump to other names" else "Jump to $letter"
                        if (enabled) onClick { onJump(letter); true } else disabled()
                    },
                contentAlignment = Alignment.Center,
            ) {
                Text(
                    letter,
                    style = MaterialTheme.typography.labelSmall,
                    fontWeight = FontWeight.SemiBold,
                    // A letter you can jump to is tappable, so it is the tint; an empty one recedes.
                    color = if (enabled) colors.primary else colors.mutedForegroundDim,
                )
            }
        }
    }
}

/** The bucket for names that do not start with a Latin letter; "number" is not what it holds. */
private const val OTHER_NAMES = "#"

/** iOS puts `#` last; so does the web's phone index. */
private val INDEX_LETTERS: List<String> = ('A'..'Z').map { it.toString() } + OTHER_NAMES

/** 16dp a letter, the iOS index pitch: 27 of them fit between the header and the player. */
private val INDEX_LETTER_HEIGHT = 16.dp

/**
 * The trailing sort / filter button of a page header, and the menu it opens: the web's single
 * "Sort and filter" / "View options" pull-down. A borderless 48dp icon button, as Material draws
 * a top bar's actions; [content] gets a `close` for the items that should dismiss it.
 */
@Composable
fun MhMenuButton(
    icon: ImageVector,
    contentDescription: String,
    modifier: Modifier = Modifier,
    content: @Composable ColumnScope.(close: () -> Unit) -> Unit,
) {
    val colors = MhTheme.colors
    var expanded by remember { mutableStateOf(false) }
    Box(modifier = modifier) {
        IconButton(onClick = { expanded = true }) {
            Icon(icon, contentDescription = contentDescription, tint = colors.foreground)
        }
        DropdownMenu(
            expanded = expanded,
            onDismissRequest = { expanded = false },
            shape = MhMenuShape,
            containerColor = colors.popover,
        ) {
            content { expanded = false }
        }
    }
}

/** A group heading inside a menu ("Sort by") — the web's `DropdownMenu.GroupHeading`. */
@Composable
fun MhMenuHeading(label: String) {
    Text(
        label,
        style = MaterialTheme.typography.labelMedium,
        color = MhTheme.colors.mutedForeground,
        modifier = Modifier
            .padding(start = 12.dp, end = 12.dp, top = 8.dp, bottom = 4.dp)
            .semantics { heading() },
    )
}

/**
 * A menu item that is one choice of several ([toggle] false: a sort key, a direction) or an
 * on/off switch ([toggle] true: "Unreleased only"). Every item keeps a leading check slot so the
 * labels line up whether or not they are chosen, and TalkBack hears a radio button or a checkbox
 * rather than a plain item. [count] is the web's trailing tabular count.
 */
@Composable
fun MhMenuCheckItem(
    label: String,
    checked: Boolean,
    onClick: () -> Unit,
    toggle: Boolean = false,
    supporting: String? = null,
    count: Int? = null,
    /** False shows the item unavailable (dimmed, inert): a filter that could only empty the list. */
    enabled: Boolean = true,
) {
    val colors = MhTheme.colors
    DropdownMenuItem(
        enabled = enabled,
        text = {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(modifier = Modifier.weight(1f, fill = false)) {
                    Text(
                        label,
                        style = MaterialTheme.typography.bodyMedium,
                        color = if (enabled) colors.foreground else colors.mutedForegroundDim,
                    )
                    if (supporting != null) {
                        Text(
                            supporting,
                            style = MaterialTheme.typography.bodySmall,
                            color = colors.mutedForeground,
                        )
                    }
                }
                if (count != null) {
                    Spacer(Modifier.weight(1f))
                    Spacer(Modifier.width(16.dp))
                    Text(
                        count.formatGrouped(),
                        style = MaterialTheme.typography.bodyMedium.copy(fontFeatureSettings = "tnum"),
                        color = colors.mutedForeground,
                    )
                }
            }
        },
        leadingIcon = {
            Box(modifier = Modifier.size(20.dp)) {
                if (checked) {
                    Icon(Icons.Rounded.Check, contentDescription = null, tint = colors.primary)
                }
            }
        },
        onClick = onClick,
        modifier = Modifier.semantics {
            if (toggle) {
                role = Role.Checkbox
                toggleableState = ToggleableState(checked)
            } else {
                role = Role.RadioButton
                selected = checked
            }
        },
    )
}

/** A plain menu action ("Clear filters") in the same menus, lined up with the check items. */
@Composable
fun MhMenuActionItem(label: String, onClick: () -> Unit) {
    DropdownMenuItem(
        text = {
            Text(label, style = MaterialTheme.typography.bodyMedium, color = MhTheme.colors.foreground)
        },
        leadingIcon = { Spacer(Modifier.size(20.dp)) },
        onClick = onClick,
    )
}

/**
 * The web's inline `Badge` capsule: a semantic [color] as text over a light wash of [fill] — the
 * `warning` variant is `bg-warning/6 text-warning-text` (/15 in dark), which keeps the label above
 * 4.5:1 on white and on grouped surfaces alike. Sentence case, like every label the two clients
 * share.
 */
@Composable
fun MhBadge(label: String, color: Color, fill: Color, modifier: Modifier = Modifier) {
    Box(
        modifier = modifier
            .clip(CircleShape)
            .background(fill)
            .padding(horizontal = 7.dp, vertical = 1.dp),
    ) {
        Text(
            label,
            style = MaterialTheme.typography.labelMedium,
            color = color,
            maxLines = 1,
        )
    }
}

/**
 * The three-bar now-playing equalizer the web draws on the loaded row — over its art, or in place
 * of the track number where a row has none. [color] defaults to the tint; over artwork it is white,
 * because the dark scrim under it would swallow the light theme's deep green.
 */
@Composable
fun EqualizerBars(playing: Boolean, modifier: Modifier = Modifier, color: Color = Color.Unspecified) {
    val barColor = color.takeOrElse { MhTheme.colors.primary }
    Row(
        modifier = modifier.height(13.dp),
        verticalAlignment = Alignment.Bottom,
        horizontalArrangement = Arrangement.spacedBy(2.dp),
    ) {
        // Only a playing row owns the infinite transition. A transition keeps animating — and
        // asking for a frame every vsync — whether or not its values are read, so a paused row
        // holding one kept the list redrawing at 60fps for bars that never moved.
        if (playing) {
            val transition = rememberInfiniteTransition(label = "equalizer")
            // Staggered phases, matching the web's -0.5s / -0.2s / -0.7s animation delays.
            listOf(0, 300, 200).forEachIndexed { index, offset ->
                val fraction by transition.animateFloat(
                    initialValue = if (index % 2 == 0) 0.3f else 1f,
                    targetValue = if (index % 2 == 0) 1f else 0.3f,
                    animationSpec = infiniteRepeatable(
                        animation = tween(durationMillis = 900, delayMillis = 0),
                        repeatMode = RepeatMode.Reverse,
                        initialStartOffset = StartOffset(offset),
                    ),
                    label = "bar$index",
                )
                EqualizerBar(fraction, barColor)
            }
        } else {
            // Uneven, so a paused row reads as a stopped equalizer rather than an ellipsis.
            for (fraction in PAUSED_BARS) EqualizerBar(fraction, barColor)
        }
    }
}

private val PAUSED_BARS = listOf(0.45f, 0.9f, 0.65f)

@Composable
private fun EqualizerBar(fraction: Float, color: Color) {
    Box(
        modifier = Modifier
            .width(2.5.dp)
            .height(13.dp * fraction)
            .clip(RoundedCornerShape(1.dp))
            .background(color),
    )
}

/** Thousands separators, matching the web's `toLocaleString()` counts. */
fun Int.formatGrouped(): String = "%,d".format(this)
