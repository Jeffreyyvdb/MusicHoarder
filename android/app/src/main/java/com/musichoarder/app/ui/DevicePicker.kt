package com.musichoarder.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Check
import androidx.compose.material.icons.rounded.Laptop
import androidx.compose.material.icons.rounded.Smartphone
import androidx.compose.material.icons.rounded.Speaker
import androidx.compose.material.icons.rounded.Tablet
import androidx.compose.material3.BottomSheetDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.Immutable
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.musichoarder.app.data.DeviceKind
import com.musichoarder.app.data.DevicePicker
import com.musichoarder.app.data.DevicePickerRow
import com.musichoarder.app.ui.theme.MhTheme
import kotlinx.coroutines.launch

/**
 * What a Devices control needs: the picker's rows (`devicePickerFor`, the web's `entries` rule),
 * this phone's own name and icon for the "This device" row, and what a choice does — null is
 * "This device" (pick the music up here), a row asks that device to take over.
 */
@Immutable
class DevicesControl(
    val picker: DevicePicker,
    val thisDeviceName: String,
    val thisDeviceKind: String,
    val onChoose: (DevicePickerRow?) -> Unit,
)

/** The glyph for a device kind — the web's `DEVICE_ICONS` (lucide Laptop / Smartphone / Tablet / Speaker). */
fun deviceIcon(kind: String): ImageVector = when (kind) {
    DeviceKind.COMPUTER -> Icons.Rounded.Laptop
    DeviceKind.PHONE -> Icons.Rounded.Smartphone
    DeviceKind.TABLET -> Icons.Rounded.Tablet
    else -> Icons.Rounded.Speaker
}

/**
 * Where the account's music plays — the web's `DevicePicker` on a phone: a sheet titled "Play on",
 * "This device" first (its own name under it, checked while the music is here), then the other
 * devices open right now, the one holding the session checked and marked Playing or Paused.
 * Choosing closes the sheet: this device pulls the music here at its position (same queue, same
 * station); another is asked to take over.
 *
 * Its colours are whatever [MhTheme] provides where it is opened: the app's palette under the mini
 * player, the plain dark one inside Now Playing (as the web gives its nested sheet `dark`).
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DevicePickerSheet(control: DevicesControl, onDismiss: () -> Unit) {
    val colors = MhTheme.colors
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val scope = rememberCoroutineScope()
    // Slide down first, then act, so the choice is not made under a sheet still on screen.
    val choose: (DevicePickerRow?) -> Unit = { row ->
        scope.launch { sheetState.hide() }.invokeOnCompletion {
            onDismiss()
            control.onChoose(row)
        }
    }

    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState = sheetState,
        containerColor = colors.sheet,
        contentColor = colors.foreground,
        dragHandle = { BottomSheetDefaults.DragHandle(color = colors.mutedForegroundDim) },
    ) {
        Column(modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp).padding(bottom = 16.dp)) {
            Text(
                "Play on",
                style = MaterialTheme.typography.titleMedium,
                fontWeight = FontWeight.SemiBold,
                color = colors.foreground,
                modifier = Modifier
                    .padding(start = 4.dp, bottom = 12.dp)
                    .semantics { heading() },
            )
            // One grouped cell list, the web's `GroupedList.Section`: cells on the elevated card
            // inside a sheet, hairlines inset past the icon.
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .clip(RoundedCornerShape(12.dp))
                    .background(colors.cardElevated),
            ) {
                DeviceRow(
                    label = "This device",
                    status = control.thisDeviceName,
                    statusLive = false,
                    kind = control.thisDeviceKind,
                    current = control.picker.thisDeviceCurrent,
                    onClick = { choose(null) },
                )
                for (row in control.picker.others) {
                    HorizontalDivider(color = colors.separator, modifier = Modifier.padding(start = 57.dp))
                    DeviceRow(
                        label = row.name,
                        status = row.status,
                        // Live state is tinted (the one playing); everything else is secondary text.
                        statusLive = row.status == DEVICE_STATUS_PLAYING,
                        kind = row.kind,
                        current = row.current,
                        onClick = { choose(row) },
                    )
                }
            }
            if (control.picker.others.isEmpty()) {
                Text(
                    "Other devices appear here while MusicHoarder is open on them.",
                    style = MaterialTheme.typography.bodySmall,
                    color = colors.mutedForeground,
                    modifier = Modifier.padding(start = 16.dp, end = 16.dp, top = 8.dp),
                )
            }
        }
    }
}

/** The picker's "Playing" status word — the one tinted as live state. */
private const val DEVICE_STATUS_PLAYING = "Playing"

@Composable
private fun DeviceRow(
    label: String,
    status: String?,
    statusLive: Boolean,
    kind: String,
    current: Boolean,
    onClick: () -> Unit,
) {
    val colors = MhTheme.colors
    Row(
        modifier = Modifier
            .fillMaxWidth()
            // One choice of several, so TalkBack hears which one the music is on.
            .selectable(selected = current, role = Role.RadioButton, onClick = onClick)
            .heightIn(min = 56.dp)
            .padding(horizontal = 14.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        // The web's 29pt rounded tile around the kind glyph.
        Box(
            modifier = Modifier
                .size(29.dp)
                .clip(RoundedCornerShape(7.dp))
                .background(colors.muted),
            contentAlignment = Alignment.Center,
        ) {
            Icon(deviceIcon(kind), contentDescription = null, tint = colors.foreground, modifier = Modifier.size(18.dp))
        }
        Spacer(Modifier.width(14.dp))
        Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.Center) {
            Text(
                label,
                style = MaterialTheme.typography.bodyLarge,
                color = colors.foreground,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            if (!status.isNullOrBlank()) {
                Text(
                    status,
                    style = MaterialTheme.typography.bodyMedium,
                    color = if (statusLive) colors.primary else colors.mutedForeground,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        }
        Box(Modifier.size(24.dp), contentAlignment = Alignment.Center) {
            if (current) {
                Icon(Icons.Rounded.Check, contentDescription = null, tint = colors.primary, modifier = Modifier.size(20.dp))
            }
        }
    }
}

/**
 * "Playing on MacBook" with the holding device's icon — under the song in the mini player and
 * under Now Playing's toggle row. A tap opens the picker, as on the web. Tinted as live state
 * while another device is playing it ([live]); secondary text while it is paused or remembered.
 */
@Composable
fun DeviceLine(
    text: String,
    kind: String,
    live: Boolean,
    style: TextStyle,
    onClick: (() -> Unit)?,
    modifier: Modifier = Modifier,
    /** Inside the tap target, so padding grows the target rather than shrinking it. */
    contentPadding: PaddingValues = PaddingValues(0.dp),
) {
    val colors = MhTheme.colors
    val tint: Color = if (live) colors.primary else colors.mutedForeground
    Row(
        modifier = modifier
            .then(
                if (onClick != null) {
                    Modifier
                        .clip(RoundedCornerShape(8.dp))
                        .clickable(onClickLabel = "Choose a device", role = Role.Button, onClick = onClick)
                } else {
                    Modifier
                },
            )
            .padding(contentPadding),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(deviceIcon(kind), contentDescription = null, tint = tint, modifier = Modifier.size(14.dp))
        Spacer(Modifier.width(5.dp))
        Text(text, style = style, color = tint, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}
