package com.musichoarder.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.PlaylistAdd
import androidx.compose.material3.BottomSheetDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.musichoarder.app.data.Playlist
import com.musichoarder.app.data.PlaylistsState
import com.musichoarder.app.data.Track
import com.musichoarder.app.data.playlistSubtitle
import com.musichoarder.app.data.recentFirst
import com.musichoarder.app.ui.theme.MhTheme
import kotlinx.coroutines.launch

/** What "Add to playlist…" was asked for: the tracks, and a name for them in the sheet's subtitle. */
data class AddToPlaylistRequest(val songIds: List<Int>, val label: String)

/**
 * "Add to playlist" — the web's `AddToPlaylistSheet`: New playlist… first, then every playlist, most
 * recently changed first. A synced playlist takes additions too; they play after its own tracks.
 * Picking one slides the sheet down, then adds (the result arrives as a snackbar).
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AddToPlaylistSheet(
    request: AddToPlaylistRequest,
    playlists: List<Playlist>,
    state: PlaylistsState,
    coverUrl: (Track, Int) -> String?,
    onPick: (Playlist) -> Unit,
    onCreate: (String) -> Unit,
    onDismiss: () -> Unit,
) {
    val colors = MhTheme.colors
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val scope = rememberCoroutineScope()
    var naming by rememberSaveable { mutableStateOf(false) }
    val ordered = remember(playlists) { recentFirst(playlists) }
    val what = if (request.songIds.size == 1) request.label else "${request.songIds.size} tracks"

    // Slide down first, then act, so the choice is not made under a sheet still on screen.
    val thenClose: (() -> Unit) -> Unit = { action ->
        scope.launch { sheetState.hide() }.invokeOnCompletion {
            action()
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
                "Add to playlist",
                style = MaterialTheme.typography.titleMedium,
                fontWeight = FontWeight.SemiBold,
                color = colors.foreground,
                modifier = Modifier.padding(start = 4.dp).semantics { heading() },
            )
            Text(
                what,
                style = MaterialTheme.typography.bodyMedium,
                color = colors.mutedForeground,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.padding(start = 4.dp, bottom = 12.dp),
            )
            LazyColumn(
                modifier = Modifier
                    .weight(1f, fill = false)
                    .fillMaxWidth()
                    .clip(RoundedCornerShape(12.dp))
                    .background(colors.cardElevated),
            ) {
                item(key = "new") {
                    SheetRow(
                        label = "New playlist…",
                        subtitle = null,
                        leading = {
                            Box(
                                modifier = Modifier.size(44.dp).clip(RoundedCornerShape(6.dp)).background(colors.muted),
                                contentAlignment = Alignment.Center,
                            ) {
                                Icon(
                                    Icons.AutoMirrored.Rounded.PlaylistAdd,
                                    contentDescription = null,
                                    tint = colors.primary,
                                )
                            }
                        },
                        onClick = { naming = true },
                    )
                }
                items(ordered, key = { it.id }) { playlist ->
                    HorizontalDivider(color = colors.separator, modifier = Modifier.padding(start = 72.dp))
                    SheetRow(
                        label = playlist.name,
                        subtitle = playlistSubtitle(playlist.source, playlist.tracks.size),
                        leading = {
                            PlaylistArtwork(
                                playlist = playlist,
                                coverUrl = coverUrl,
                                size = 128,
                                modifier = Modifier.size(44.dp),
                                shape = RoundedCornerShape(6.dp),
                            )
                        },
                        onClick = { thenClose { onPick(playlist) } },
                    )
                }
            }
            when {
                state.isLoading && !state.loaded -> Box(
                    modifier = Modifier.fillMaxWidth().padding(16.dp),
                    contentAlignment = Alignment.Center,
                ) { CircularProgressIndicator(color = colors.primary, modifier = Modifier.size(24.dp)) }

                state.unsupported -> SheetNote("Playlists need a newer server.")
                state.error != null && !state.loaded -> SheetNote(state.error)
            }
        }
    }

    if (naming) {
        PlaylistNameDialog(
            title = "New playlist",
            confirmLabel = "Create",
            supporting = "It starts with $what.",
            onConfirm = { name ->
                naming = false
                thenClose { onCreate(name) }
            },
            onDismiss = { naming = false },
        )
    }
}

@Composable
private fun SheetRow(
    label: String,
    subtitle: String?,
    leading: @Composable () -> Unit,
    onClick: () -> Unit,
) {
    val colors = MhTheme.colors
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(role = Role.Button, onClick = onClick)
            .heightIn(min = 60.dp)
            .padding(horizontal = 14.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        leading()
        Spacer(Modifier.width(14.dp))
        Column(modifier = Modifier.weight(1f)) {
            Text(
                label,
                style = MaterialTheme.typography.bodyLarge,
                color = colors.foreground,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            if (subtitle != null) {
                Text(
                    subtitle,
                    style = MaterialTheme.typography.bodyMedium,
                    color = colors.mutedForeground,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        }
    }
}

@Composable
private fun SheetNote(text: String) {
    Text(
        text,
        style = MaterialTheme.typography.bodySmall,
        color = MhTheme.colors.mutedForeground,
        modifier = Modifier.padding(start = 16.dp, end = 16.dp, top = 8.dp),
    )
}
