package com.musichoarder.app.ui

import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.StartOffset
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Check
import androidx.compose.material.icons.rounded.Favorite
import androidx.compose.material.icons.rounded.FavoriteBorder
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.musichoarder.app.ui.theme.MhTheme

/**
 * The widgets the four library pages add to the shell's chrome: filter chips, the A-Z index, the
 * Primary/All toggle, the sort menu, the heart and the row badges.
 *
 * Kept beside [MhTabPill] and friends in `Chrome.kt` for the same reason: it should be obvious when
 * a page invents its own variant instead of reusing the shell's.
 */

/** A filter pill with an optional count. Pressed reads as the primary tint, as on the web. */
@Composable
fun MhFilterChip(
    label: String,
    pressed: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    icon: ImageVector? = null,
    count: Int? = null,
) {
    val colors = MhTheme.colors
    Row(
        modifier = modifier
            .height(32.dp)
            .clip(CircleShape)
            .background(if (pressed) colors.primary.copy(alpha = 0.10f) else colors.card)
            .border(1.dp, if (pressed) colors.primary else colors.border, CircleShape)
            .clickable(onClick = onClick)
            .padding(horizontal = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        val tint = if (pressed) colors.primary else colors.mutedForeground
        if (icon != null) {
            Icon(icon, contentDescription = null, tint = tint, modifier = Modifier.size(14.dp))
        }
        Text(
            label,
            style = MaterialTheme.typography.labelLarge,
            fontWeight = if (pressed) FontWeight.Medium else FontWeight.Normal,
            color = tint,
        )
        if (count != null) {
            Text(
                count.formatGrouped(),
                style = MaterialTheme.typography.labelLarge,
                color = tint.copy(alpha = 0.6f),
            )
        }
    }
}

/**
 * The A-Z index above the artist grid.
 *
 * Wrapped rather than scrolled: an index you have to scroll through to reach "W" is not an index.
 * Letters nobody falls under are greyed and inert, and unlike the web there is a `#` button, so the
 * artists whose names do not start with a Latin letter are reachable without going back to All.
 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
fun MhAlphabetBar(
    selected: String?,
    present: Set<String>,
    onSelect: (String?) -> Unit,
    modifier: Modifier = Modifier,
) {
    FlowRow(
        modifier = modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.spacedBy(2.dp),
        verticalArrangement = Arrangement.spacedBy(2.dp),
    ) {
        LetterButton("All", selected == null, enabled = true) { onSelect(null) }
        for (letter in ALPHABET) {
            val enabled = letter in present
            LetterButton(letter, selected == letter, enabled) { onSelect(letter) }
        }
    }
}

private val ALPHABET: List<String> =
    ('A'..'Z').map { it.toString() } + "#"

@Composable
private fun LetterButton(label: String, selected: Boolean, enabled: Boolean, onClick: () -> Unit) {
    val colors = MhTheme.colors
    Box(
        modifier = Modifier
            .widthIn(min = 28.dp)
            .heightIn(min = 28.dp)
            .clip(RoundedCornerShape(4.dp))
            .background(if (selected) colors.muted else Color.Transparent)
            .then(if (enabled) Modifier.clickable(onClick = onClick) else Modifier)
            .padding(horizontal = 6.dp, vertical = 5.dp),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            label,
            style = MaterialTheme.typography.bodySmall,
            fontFamily = FontFamily.Monospace,
            fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Normal,
            color = when {
                selected -> colors.foreground
                enabled -> colors.mutedForeground
                else -> colors.mutedForeground.copy(alpha = 0.4f)
            },
        )
    }
}

/** The Primary / All segmented control on the artist grid. */
@Composable
fun MhSegmented(
    options: List<Pair<String, Boolean>>,
    onSelect: (Int) -> Unit,
    modifier: Modifier = Modifier,
) {
    val colors = MhTheme.colors
    Row(
        modifier = modifier
            .clip(RoundedCornerShape(6.dp))
            .border(1.dp, colors.border, RoundedCornerShape(6.dp))
            .padding(2.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        options.forEachIndexed { index, (label, selected) ->
            Box(
                modifier = Modifier
                    .clip(RoundedCornerShape(4.dp))
                    .background(if (selected) colors.muted else Color.Transparent)
                    .clickable { onSelect(index) }
                    .padding(horizontal = 10.dp, vertical = 4.dp),
            ) {
                Text(
                    label,
                    style = MaterialTheme.typography.bodySmall,
                    fontFamily = FontFamily.Monospace,
                    fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Normal,
                    color = if (selected) colors.foreground else colors.mutedForeground,
                )
            }
        }
    }
}

/** A pill that opens a menu of orderings, showing the current one. */
@Composable
fun <T> MhSortPill(
    current: T,
    options: List<Pair<T, String>>,
    onSelect: (T) -> Unit,
    modifier: Modifier = Modifier,
    icon: ImageVector? = null,
    trailing: String? = null,
) {
    val colors = MhTheme.colors
    var expanded by remember { mutableStateOf(false) }
    Box(modifier = modifier) {
        Row(
            modifier = Modifier
                .height(32.dp)
                .clip(CircleShape)
                .background(colors.card)
                .border(1.dp, colors.border, CircleShape)
                .clickable { expanded = true }
                .padding(horizontal = 12.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(6.dp),
        ) {
            if (icon != null) {
                Icon(
                    icon,
                    contentDescription = null,
                    tint = colors.mutedForeground,
                    modifier = Modifier.size(14.dp),
                )
            }
            Text(
                options.firstOrNull { it.first == current }?.second.orEmpty() + trailing.orEmpty(),
                style = MaterialTheme.typography.labelLarge,
                color = colors.foreground,
            )
        }
        DropdownMenu(
            expanded = expanded,
            onDismissRequest = { expanded = false },
            containerColor = colors.popover,
        ) {
            for ((value, label) in options) {
                DropdownMenuItem(
                    text = {
                        Text(
                            label,
                            style = MaterialTheme.typography.bodyMedium,
                            color = colors.foreground,
                        )
                    },
                    trailingIcon = {
                        if (value == current) {
                            Icon(
                                Icons.Rounded.Check,
                                contentDescription = null,
                                tint = colors.primary,
                                modifier = Modifier.size(16.dp),
                            )
                        }
                    },
                    onClick = {
                        expanded = false
                        onSelect(value)
                    },
                )
            }
        }
    }
}

/**
 * The like toggle.
 *
 * The web only reveals this on hover, which a phone cannot do, so it is always drawn: filled and
 * green when liked, a dim outline when not.
 */
@Composable
fun HeartButton(liked: Boolean, onClick: () -> Unit, modifier: Modifier = Modifier) {
    val colors = MhTheme.colors
    Box(
        modifier = modifier
            .size(40.dp)
            .clip(CircleShape)
            .clickable(onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        Icon(
            if (liked) Icons.Rounded.Favorite else Icons.Rounded.FavoriteBorder,
            contentDescription = if (liked) "Remove from liked songs" else "Add to liked songs",
            tint = if (liked) colors.primary else colors.mutedForeground.copy(alpha = 0.55f),
            modifier = Modifier.size(17.dp),
        )
    }
}

/** The small monospace tags on a track row: `REVIEW`, `LRC`. */
@Composable
fun MhMonoBadge(label: String, modifier: Modifier = Modifier, tint: Color? = null) {
    val colors = MhTheme.colors
    val foreground = tint ?: colors.mutedForeground
    Box(
        modifier = modifier
            .clip(RoundedCornerShape(3.dp))
            .background(foreground.copy(alpha = 0.15f))
            .padding(horizontal = 4.dp, vertical = 1.dp),
    ) {
        Text(
            label,
            style = MaterialTheme.typography.labelSmall,
            fontFamily = FontFamily.Monospace,
            fontWeight = FontWeight.SemiBold,
            color = foreground,
        )
    }
}

/** The three-bar now-playing equalizer the web draws in place of a row's index. */
@Composable
fun EqualizerBars(playing: Boolean, modifier: Modifier = Modifier) {
    val colors = MhTheme.colors
    val transition = rememberInfiniteTransition(label = "equalizer")
    Row(
        modifier = modifier.height(13.dp),
        verticalAlignment = Alignment.Bottom,
        horizontalArrangement = Arrangement.spacedBy(2.dp),
    ) {
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
            Box(
                modifier = Modifier
                    .width(2.5.dp)
                    .height(13.dp * if (playing) fraction else 0.35f)
                    .clip(RoundedCornerShape(1.dp))
                    .background(colors.primary),
            )
        }
    }
}

/** Thousands separators, matching the web's `toLocaleString()` counts. */
fun Int.formatGrouped(): String = "%,d".format(this)
