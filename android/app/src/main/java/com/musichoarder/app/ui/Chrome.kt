package com.musichoarder.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Close
import androidx.compose.material.icons.rounded.PlayArrow
import androidx.compose.material.icons.rounded.Search
import androidx.compose.material.icons.rounded.Shuffle
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.minimumInteractiveComponentSize
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.musichoarder.app.ui.theme.MhTheme

/**
 * The web shell's chrome, rebuilt for Compose: the large-title header each tab scrolls away, the
 * capsule search field, the Play / Shuffle pair and the removable filter token. Keeping these in
 * one file makes it obvious when a screen invents its own variant instead of reusing the shell's.
 */

/**
 * A page's header: a 28sp title, a short muted meta line, the page's trailing actions, and
 * whatever the page stacks under it (search, filter tokens, the letter index).
 *
 * The port of the web's compact nav bar + large title (`PageToolbarV2`), but laid out as the
 * list's FIRST item rather than a pinned bar: it scrolls away with the rows, so a phone's first
 * screen is spent on music rather than chrome. The actions ride on the title's row instead of a
 * bar of their own above it, which is the one place this differs from the web — a 44dp row
 * holding nothing but two glyphs is height a 411dp screen does not have to spare.
 */
@Composable
fun MhLargeHeader(
    title: String,
    meta: String?,
    modifier: Modifier = Modifier,
    actions: @Composable RowScope.() -> Unit = {},
    /** True while another bar is showing the same [actions]: these drop out of TalkBack's reach. */
    actionsHidden: Boolean = false,
    content: @Composable ColumnScope.() -> Unit = {},
) {
    val colors = MhTheme.colors
    Column(modifier = modifier.fillMaxWidth().padding(bottom = 8.dp)) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(start = 16.dp, end = 4.dp, top = 8.dp, bottom = 8.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    title,
                    style = MaterialTheme.typography.headlineLarge,
                    color = colors.foreground,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.semantics { heading() },
                )
                if (!meta.isNullOrEmpty()) {
                    Text(
                        meta,
                        style = MaterialTheme.typography.bodyMedium,
                        color = colors.mutedForeground,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
            }
            Row(
                modifier = if (actionsHidden) Modifier.clearAndSetSemantics {} else Modifier,
                verticalAlignment = Alignment.CenterVertically,
                content = actions,
            )
        }
        content()
    }
}

/**
 * The capsule search field: 48dp tall (Material's touch floor — the web's is 44pt, Apple's), 16sp
 * text (the web's `text-base` below md, the size iOS never zooms on), a fill with no stroke.
 *
 * The keyboard is set up the way the web's `SearchField` sets `enterkeyhint="search"`,
 * `autocapitalize="off"` and `autocorrect="off"`: names of artists and albums are exactly what
 * autocorrect mangles, and the action key closes the keyboard because the list already filters as
 * you type — there is nothing to submit. Clear is a full 48dp target around the 18dp glyph.
 */
@Composable
fun MhSearchField(
    value: String,
    onValueChange: (String) -> Unit,
    placeholder: String,
    modifier: Modifier = Modifier,
) {
    val colors = MhTheme.colors
    val focusManager = LocalFocusManager.current
    Row(
        modifier = modifier
            .heightIn(min = 48.dp)
            .clip(CircleShape)
            // A field is a fill with no stroke (`bg-input border-transparent`). The token is
            // already translucent, so it is used as-is — see `MhColors`.
            .background(colors.input)
            .padding(start = 14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(
            Icons.Rounded.Search,
            contentDescription = null,
            tint = colors.mutedForeground,
            modifier = Modifier.size(20.dp),
        )
        Spacer(Modifier.size(8.dp))
        BasicTextField(
            value = value,
            onValueChange = onValueChange,
            singleLine = true,
            textStyle = MaterialTheme.typography.bodyLarge.copy(color = colors.foreground),
            cursorBrush = SolidColor(colors.primary),
            keyboardOptions = KeyboardOptions(
                capitalization = KeyboardCapitalization.None,
                autoCorrectEnabled = false,
                imeAction = ImeAction.Search,
            ),
            keyboardActions = KeyboardActions(onSearch = { focusManager.clearFocus() }),
            modifier = Modifier.weight(1f),
            // The placeholder lives in the decoration box, inside the field's own node, so TalkBack
            // reads it as the field's hint rather than as a stray label beside it.
            decorationBox = { field ->
                Box(contentAlignment = Alignment.CenterStart) {
                    if (value.isEmpty()) {
                        Text(
                            placeholder,
                            style = MaterialTheme.typography.bodyLarge,
                            color = colors.mutedForeground,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }
                    field()
                }
            },
        )
        if (value.isNotEmpty()) {
            Box(
                modifier = Modifier
                    .size(48.dp)
                    .clip(CircleShape)
                    .clickable(role = Role.Button) { onValueChange("") }
                    .semantics { contentDescription = "Clear search" },
                contentAlignment = Alignment.Center,
            ) {
                Icon(
                    Icons.Rounded.Close,
                    contentDescription = null,
                    tint = colors.mutedForeground,
                    modifier = Modifier.size(18.dp),
                )
            }
        } else {
            Spacer(Modifier.size(14.dp))
        }
    }
}

/**
 * Play and Shuffle as two equal gray capsules with a tint glyph and label — the web's
 * `variant="gray" size="pill"` pair, which is Apple Music's. The same pair heads the Tracks list,
 * the album page and the share viewer, so one action never has two looks. [onShuffle] null drops
 * Shuffle and lets Play take the row (a one-track share has nothing to shuffle).
 */
@Composable
fun MhPlayShufflePills(
    onPlay: () -> Unit,
    onShuffle: (() -> Unit)?,
    modifier: Modifier = Modifier,
    playLabel: String = "Play",
    playIcon: ImageVector = Icons.Rounded.PlayArrow,
) {
    Row(
        modifier = modifier.fillMaxWidth().padding(horizontal = 16.dp),
        horizontalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        MhGrayPill(playLabel, playIcon, onPlay, Modifier.weight(1f))
        if (onShuffle != null) {
            MhGrayPill("Shuffle", Icons.Rounded.Shuffle, onShuffle, Modifier.weight(1f))
        }
    }
}

@Composable
private fun MhGrayPill(label: String, icon: ImageVector, onClick: () -> Unit, modifier: Modifier) {
    val colors = MhTheme.colors
    // 44dp visual (the web's pill), inside Material's 48dp touch floor: TalkBack's focus bounds and
    // Accessibility Scanner measure the layout, not Compose's expanded touch area.
    Row(
        modifier = modifier
            .minimumInteractiveComponentSize()
            .heightIn(min = 44.dp)
            .clip(CircleShape)
            .background(colors.secondary)
            .clickable(role = Role.Button, onClick = onClick)
            .padding(horizontal = 16.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.Center,
    ) {
        Icon(icon, contentDescription = null, tint = colors.primary, modifier = Modifier.size(20.dp))
        Spacer(Modifier.size(8.dp))
        Text(
            label,
            style = MaterialTheme.typography.titleLarge,
            color = colors.primary,
            maxLines = 1,
        )
    }
}

/**
 * An active filter the page's menu (or a drill-in) turned on, shown where you can see it and take
 * it off: the web's compact filter token, `bg-primary/12 text-primary` with a trailing ✕. The
 * whole token is the remove button: 32dp visual inside a reserved 48dp
 * (`minimumInteractiveComponentSize`, as Material's own icon buttons do) — Compose's touch-bound
 * expansion alone widens the finger's target but not TalkBack's focus bounds, which follow layout.
 */
@Composable
fun MhFilterToken(
    label: String,
    clearLabel: String,
    onClear: () -> Unit,
    modifier: Modifier = Modifier,
    icon: ImageVector? = null,
) {
    val colors = MhTheme.colors
    Row(
        modifier = modifier
            .minimumInteractiveComponentSize()
            .heightIn(min = 32.dp)
            .clip(CircleShape)
            // `primary` is opaque, so an alpha here is a tint of it — unlike the translucent fills.
            .background(colors.primary.copy(alpha = 0.12f))
            .clickable(onClickLabel = clearLabel, role = Role.Button, onClick = onClear)
            .padding(start = if (icon != null) 10.dp else 12.dp, end = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        if (icon != null) {
            Icon(icon, contentDescription = null, tint = colors.primary, modifier = Modifier.size(16.dp))
        }
        Text(
            label,
            style = MaterialTheme.typography.bodyMedium,
            fontWeight = FontWeight.Medium,
            color = colors.primary,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.weight(1f, fill = false),
        )
        Icon(Icons.Rounded.Close, contentDescription = null, tint = colors.primary, modifier = Modifier.size(16.dp))
    }
}

/**
 * A tint text action beside the filter tokens ("Clear"): 32dp of text inside Material's 48dp
 * touch floor, so it lines up with the tokens and never grows the row when it appears.
 */
@Composable
fun MhTextAction(label: String, onClick: () -> Unit, modifier: Modifier = Modifier) {
    Box(
        modifier = modifier
            .minimumInteractiveComponentSize()
            .heightIn(min = 32.dp)
            .clip(CircleShape)
            .clickable(role = Role.Button, onClick = onClick)
            .padding(horizontal = 8.dp),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            label,
            style = MaterialTheme.typography.bodyMedium,
            fontWeight = FontWeight.Medium,
            color = MhTheme.colors.primary,
        )
    }
}
