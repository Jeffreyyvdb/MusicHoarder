package com.musichoarder.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Close
import androidx.compose.material.icons.rounded.Search
import androidx.compose.material3.Icon
import androidx.compose.material3.LocalTextStyle
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.foundation.rememberScrollState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.musichoarder.app.ui.theme.MhTheme

/**
 * The web shell's chrome, rebuilt for Compose: a top bar whose sections are pills, square bordered
 * icon buttons, and the pill-shaped search field the page toolbars carry. Keeping these in one file
 * makes it obvious when a screen invents its own variant instead of reusing the shell's.
 */

/** A section pill, as in the web top bar's tab strip — active gets a filled rounded-full chip. */
@Composable
fun MhTabPill(
    label: String,
    selected: Boolean,
    modifier: Modifier = Modifier,
    onClick: () -> Unit,
) {
    val colors = MhTheme.colors
    Box(
        modifier = modifier
            .clip(CircleShape)
            .background(if (selected) colors.secondary else Color.Transparent)
            .clickable(onClick = onClick)
            .padding(horizontal = 14.dp, vertical = 7.dp),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            text = label,
            style = MaterialTheme.typography.labelLarge,
            fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Medium,
            color = if (selected) colors.foreground else colors.mutedForeground,
        )
    }
}

/** `size-9 rounded-lg border` — the top bar's search / add / theme buttons. */
@Composable
fun MhIconButton(
    icon: ImageVector,
    contentDescription: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val colors = MhTheme.colors
    Box(
        modifier = modifier
            .size(36.dp)
            .clip(RoundedCornerShape(8.dp))
            .border(1.dp, colors.border, RoundedCornerShape(8.dp))
            .clickable(onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        Icon(
            icon,
            contentDescription = contentDescription,
            tint = colors.mutedForeground,
            modifier = Modifier.size(17.dp),
        )
    }
}

/** The rounded-full search input the page toolbars use. */
@Composable
fun MhSearchField(
    value: String,
    onValueChange: (String) -> Unit,
    placeholder: String,
    modifier: Modifier = Modifier,
) {
    val colors = MhTheme.colors
    Row(
        modifier = modifier
            .height(36.dp)
            .clip(CircleShape)
            .background(colors.input.copy(alpha = if (colors.isDark) 0.6f else 1f))
            .border(1.dp, colors.border, CircleShape)
            .padding(horizontal = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(
            Icons.Rounded.Search,
            contentDescription = null,
            tint = colors.mutedForeground,
            modifier = Modifier.size(15.dp),
        )
        Spacer(Modifier.size(8.dp))
        Box(modifier = Modifier.weight(1f), contentAlignment = Alignment.CenterStart) {
            if (value.isEmpty()) {
                Text(
                    placeholder,
                    style = MaterialTheme.typography.bodyMedium,
                    color = colors.mutedForeground,
                )
            }
            BasicTextField(
                value = value,
                onValueChange = onValueChange,
                singleLine = true,
                textStyle = LocalTextStyle.current.merge(
                    MaterialTheme.typography.bodyMedium.copy(color = colors.foreground)
                ),
                cursorBrush = SolidColor(colors.primary),
                interactionSource = remember { MutableInteractionSource() },
                modifier = Modifier.fillMaxWidth(),
            )
        }
        if (value.isNotEmpty()) {
            Spacer(Modifier.size(6.dp))
            Icon(
                Icons.Rounded.Close,
                contentDescription = "Clear search",
                tint = colors.mutedForeground,
                modifier = Modifier
                    .size(15.dp)
                    .clickable { onValueChange("") },
            )
        }
    }
}

/** Page toolbar: a muted glyph, a bold title, then whatever the page puts on the right. */
@Composable
fun MhPageToolbar(
    icon: ImageVector,
    title: String,
    modifier: Modifier = Modifier,
    trailing: @Composable RowScope.() -> Unit = {},
) {
    Row(
        modifier = modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Icon(
            icon,
            contentDescription = null,
            tint = MhTheme.colors.mutedForeground,
            modifier = Modifier.size(17.dp),
        )
        Text(
            title,
            style = MaterialTheme.typography.titleMedium,
            color = MhTheme.colors.foreground,
        )
        trailing()
    }
}

/**
 * The web's segmented tab strip: a `foreground/5` track with one pill per tab, the active one
 * filled with the page colour and lifted a hair. Used by the player's Song / Lyrics / Video
 * switcher, and by anything else that needs the same control.
 *
 * The strip scrolls when it does not fit and centres itself when it does — the same behaviour as
 * `Tabs.List`'s `mx-auto w-max` inside an `overflow-x-auto` scroller.
 */
@Composable
fun MhPillTabs(
    labels: List<String>,
    selectedIndex: Int,
    onSelect: (Int) -> Unit,
    modifier: Modifier = Modifier,
) {
    val colors = MhTheme.colors
    Box(modifier = modifier, contentAlignment = Alignment.Center) {
        Row(
            modifier = Modifier
                .horizontalScroll(rememberScrollState())
                .clip(CircleShape)
                .background(colors.foreground.copy(alpha = 0.05f))
                .padding(4.dp),
            horizontalArrangement = Arrangement.spacedBy(4.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            labels.forEachIndexed { index, label ->
                val selected = index == selectedIndex
                Box(
                    modifier = Modifier
                        .then(
                            if (selected) Modifier.shadow(1.dp, CircleShape) else Modifier
                        )
                        .clip(CircleShape)
                        .background(if (selected) colors.background else Color.Transparent)
                        .clickable { onSelect(index) }
                        .padding(horizontal = 12.dp, vertical = 6.dp),
                    contentAlignment = Alignment.Center,
                ) {
                    Text(
                        text = label,
                        style = MaterialTheme.typography.labelMedium,
                        fontWeight = FontWeight.Medium,
                        color = if (selected) colors.foreground else colors.mutedForeground,
                    )
                }
            }
        }
    }
}

/**
 * `size-9 rounded-full bg-foreground/5` — the round chrome buttons the overlays use (close, heart,
 * the fullscreen-lyrics collapse chevron), as opposed to [MhIconButton]'s bordered square.
 */
@Composable
fun MhCircleIconButton(
    icon: ImageVector,
    contentDescription: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    tint: Color? = null,
    groundAlpha: Float = 0.05f,
    size: Dp = 36.dp,
    iconSize: Dp = 16.dp,
) {
    val colors = MhTheme.colors
    Box(
        modifier = modifier
            .size(size)
            .clip(CircleShape)
            .background(colors.foreground.copy(alpha = groundAlpha))
            .clickable(onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        Icon(
            icon,
            contentDescription = contentDescription,
            tint = tint ?: colors.foreground,
            modifier = Modifier.size(iconSize),
        )
    }
}
