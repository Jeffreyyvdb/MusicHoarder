package com.musichoarder.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material.icons.rounded.MoreVert
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.unit.dp
import com.musichoarder.app.data.NowPlayingLinks
import com.musichoarder.app.data.Playlist
import com.musichoarder.app.data.Track
import com.musichoarder.app.data.likedNow
import com.musichoarder.app.data.missingLabel
import com.musichoarder.app.data.playlistKindLabel
import com.musichoarder.app.ui.theme.MhTheme

/**
 * One playlist, in play order — the port of the web's `PlaylistDetailV2`: the album page's shape
 * (Back, a centred hero with the cover, name, kind and Play / Shuffle) over the tracks. A synced
 * playlist lists its own tracks first, then "Added here"; only those added here can be removed
 * (the rest are the remote playlist's), and only a playlist made here can be renamed or deleted.
 */
@Composable
fun PlaylistScreen(
    playlist: Playlist,
    coverUrl: (Track, Int) -> String?,
    playingTrackId: Int?,
    isPlayingNow: Boolean,
    likes: Map<Int, String?>,
    onToggleLike: (Track) -> Unit,
    /** Play / Shuffle: from the top. */
    onPlay: (List<Track>, Int) -> Unit,
    onShuffle: (List<Track>) -> Unit,
    /** A row tap, which follows the row tap rule (see `RowTap`). */
    onActivateRow: (List<Track>, Int) -> Unit,
    linksOf: (Track) -> NowPlayingLinks?,
    onOpenAlbumKey: (String) -> Unit,
    onOpenArtist: (String) -> Unit,
    onAddToPlaylist: (Track) -> Unit,
    onRemove: (Track) -> Unit,
    onRename: (String) -> Unit,
    onDelete: () -> Unit,
    onBack: () -> Unit,
    contentPadding: PaddingValues,
    modifier: Modifier = Modifier,
) {
    val colors = MhTheme.colors
    val listState = rememberLazyListState()
    val tracks = playlist.tracks
    val synced = playlist.source != null
    // Where "Added here" starts in a synced playlist.
    val firstAdded = remember(tracks, playlist.addedIds, synced) {
        if (synced) tracks.indexOfFirst { it.id in playlist.addedIds } else -1
    }
    var renaming by rememberSaveable { mutableStateOf(false) }
    var confirmingDelete by rememberSaveable { mutableStateOf(false) }

    Column(modifier = modifier.fillMaxSize().background(colors.background)) {
        DrillInTopBar(
            title = playlist.name,
            listState = listState,
            navigationIcon = Icons.AutoMirrored.Rounded.ArrowBack,
            navigationLabel = "Back",
            onNavigate = onBack,
            actions = {
                // A synced playlist is named after its source and goes with it, so it has nothing here.
                if (!synced) {
                    MhMenuButton(Icons.Rounded.MoreVert, "More options for ${playlist.name}") { close ->
                        MhMenuActionItem("Rename…") { close(); renaming = true }
                        MhMenuActionItem("Delete playlist…") { close(); confirmingDelete = true }
                    }
                }
            },
        )

        LazyColumn(state = listState, modifier = Modifier.fillMaxSize(), contentPadding = contentPadding) {
            item(key = "hero", contentType = "hero") {
                AlbumHero(
                    coverUrl = null,
                    artist = playlistKindLabel(playlist.source),
                    title = playlist.name,
                    onOpenArtist = null,
                    metaLines = listOfNotNull(missingLabel(playlist.source, playlist.missingCount)),
                    onPlay = { onPlay(tracks, 0) },
                    onShuffle = { onShuffle(tracks) },
                    showPlayButtons = tracks.isNotEmpty(),
                    artwork = { artModifier, shape ->
                        PlaylistArtwork(playlist, coverUrl, size = 640, modifier = artModifier, shape = shape)
                    },
                )
            }

            if (tracks.isEmpty()) {
                item(key = "empty", contentType = "message") {
                    ListMessage(
                        when {
                            synced && playlist.missingCount > 0 ->
                                "None of its tracks are in your library yet. They play here as the wishlist brings them in."
                            synced -> "Tracks you add from any track's ⋮ menu play here too."
                            else -> "This playlist is empty.\n\nPick Add to playlist… from any track's ⋮ menu."
                        },
                    )
                }
            }

            itemsIndexed(tracks, key = { _, track -> track.id }) { index, track ->
                if (index == firstAdded && index > 0) AddedHereHeading()
                val links = linksOf(track)
                TrackRow(
                    track = track,
                    coverUrl = coverUrl(track, 128),
                    isPlaying = track.id == playingTrackId,
                    isPlayingNow = isPlayingNow,
                    liked = likedNow(likes, track),
                    onToggleLike = { onToggleLike(track) },
                    onClick = { onActivateRow(tracks, index) },
                    onOpenAlbum = links?.let { { onOpenAlbumKey(it.albumKey) } },
                    onOpenArtist = links?.let { { onOpenArtist(it.artist) } },
                    onAddToPlaylist = { onAddToPlaylist(track) },
                    onRemoveFromPlaylist = if (track.id in playlist.addedIds) {
                        { onRemove(track) }
                    } else {
                        null
                    },
                )
                if (index < tracks.lastIndex && index + 1 != firstAdded) TrackRowSeparator()
            }

            if (tracks.isNotEmpty()) {
                item(key = "summary", contentType = "footer") {
                    TracklistSummary(tracks)
                }
            }
        }
    }

    if (renaming) {
        PlaylistNameDialog(
            title = "Rename playlist",
            confirmLabel = "Save",
            initialName = playlist.name,
            onConfirm = { name -> renaming = false; onRename(name) },
            onDismiss = { renaming = false },
        )
    }
    if (confirmingDelete) {
        AlertDialog(
            onDismissRequest = { confirmingDelete = false },
            title = { Text("Delete “${playlist.name}”?") },
            text = {
                Text(
                    "The playlist goes, and its file in your library's Playlists folder with it. " +
                        "The tracks stay in your library.",
                )
            },
            confirmButton = {
                TextButton(onClick = { confirmingDelete = false; onDelete() }) {
                    Text("Delete playlist", color = colors.destructiveText)
                }
            },
            dismissButton = {
                TextButton(onClick = { confirmingDelete = false }) { Text("Cancel") }
            },
        )
    }
}

@Composable
private fun AddedHereHeading() {
    Text(
        "Added here",
        style = MaterialTheme.typography.labelMedium,
        fontWeight = FontWeight.SemiBold,
        color = MhTheme.colors.mutedForeground,
        modifier = Modifier
            .fillMaxWidth()
            .padding(start = 16.dp, end = 16.dp, top = 20.dp, bottom = 4.dp)
            .semantics { heading() },
    )
}

/** Name a playlist: "New playlist" and "Rename". Confirm stays off until there is a (changed) name. */
@Composable
fun PlaylistNameDialog(
    title: String,
    confirmLabel: String,
    onConfirm: (String) -> Unit,
    onDismiss: () -> Unit,
    initialName: String = "",
    supporting: String? = null,
) {
    val colors = MhTheme.colors
    var name by rememberSaveable { mutableStateOf(initialName) }
    val focus = remember { FocusRequester() }
    val canConfirm = name.isNotBlank() && name.trim() != initialName.trim()
    LaunchedEffect(Unit) { focus.requestFocus() }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = {
            Column {
                if (supporting != null) {
                    Text(
                        supporting,
                        style = MaterialTheme.typography.bodyMedium,
                        color = colors.mutedForeground,
                        modifier = Modifier.padding(bottom = 12.dp),
                    )
                }
                OutlinedTextField(
                    value = name,
                    onValueChange = { name = it.take(MAX_NAME_LENGTH) },
                    placeholder = { Text("Playlist name", color = colors.mutedForeground) },
                    singleLine = true,
                    shape = RoundedCornerShape(8.dp),
                    keyboardOptions = KeyboardOptions(
                        capitalization = KeyboardCapitalization.Sentences,
                        imeAction = ImeAction.Done,
                    ),
                    keyboardActions = KeyboardActions(onDone = { if (canConfirm) onConfirm(name.trim()) }),
                    colors = OutlinedTextFieldDefaults.colors(
                        focusedTextColor = colors.foreground,
                        unfocusedTextColor = colors.foreground,
                        focusedBorderColor = colors.ring,
                        unfocusedBorderColor = colors.border,
                        cursorColor = colors.primary,
                        focusedContainerColor = colors.input,
                        unfocusedContainerColor = colors.input,
                    ),
                    modifier = Modifier.fillMaxWidth().focusRequester(focus),
                )
            }
        },
        confirmButton = {
            TextButton(onClick = { onConfirm(name.trim()) }, enabled = canConfirm) { Text(confirmLabel) }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text("Cancel") }
        },
    )
}

/** The server's limit (`PlaylistsEndpoints.MaxNameLength`). */
private const val MAX_NAME_LENGTH = 200
