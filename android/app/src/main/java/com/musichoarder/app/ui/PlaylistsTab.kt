package com.musichoarder.app.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyGridState
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.grid.rememberLazyGridState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.RectangleShape
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.musichoarder.app.data.Playlist
import com.musichoarder.app.data.Track
import com.musichoarder.app.data.playlistSubtitle
import com.musichoarder.app.ui.theme.MhTheme

/**
 * The Playlists tab under the page's [header] — the port of the web's `PlaylistsV2`: the ones made
 * here first, then "Synced", the ones that follow a playlist collected from Spotify, Deezer or
 * YouTube. Square cards like the album grid; a card opens the playlist.
 */
@Composable
fun PlaylistsTab(
    playlists: List<Playlist>,
    coverUrl: (Track, Int) -> String?,
    onOpenPlaylist: (Playlist) -> Unit,
    contentPadding: PaddingValues,
    modifier: Modifier = Modifier,
    header: @Composable () -> Unit = {},
    gridState: LazyGridState = rememberLazyGridState(),
    /** Shown under the header in place of the cards; null when there are cards to show. */
    emptyMessage: String? = null,
) {
    val own = remember(playlists) { playlists.filter { it.source == null } }
    val synced = remember(playlists) { playlists.filter { it.source != null } }
    LazyVerticalGrid(
        columns = GridCells.Adaptive(minSize = 150.dp),
        state = gridState,
        modifier = modifier,
        contentPadding = PaddingValues(
            start = GRID_EDGE,
            end = GRID_EDGE,
            bottom = 16.dp + contentPadding.calculateBottomPadding(),
        ),
        horizontalArrangement = Arrangement.spacedBy(16.dp),
        verticalArrangement = Arrangement.spacedBy(22.dp),
    ) {
        item(key = "header", span = { GridItemSpan(maxLineSpan) }, contentType = "header") {
            Box(modifier = Modifier.bleedHorizontally(GRID_EDGE)) { header() }
        }
        if (emptyMessage != null) {
            item(key = "empty", span = { GridItemSpan(maxLineSpan) }, contentType = "message") {
                ListMessage(emptyMessage)
            }
            return@LazyVerticalGrid
        }
        // Section headings only when there are both kinds; one kind needs no label.
        val labelled = own.isNotEmpty() && synced.isNotEmpty()
        if (labelled) {
            item(key = "own-heading", span = { GridItemSpan(maxLineSpan) }, contentType = "heading") {
                SectionHeading("Made here", note = null)
            }
        }
        items(own, key = { it.id }, contentType = { "playlist" }) { playlist ->
            PlaylistCard(playlist, coverUrl) { onOpenPlaylist(playlist) }
        }
        if (synced.isNotEmpty()) {
            item(key = "synced-heading", span = { GridItemSpan(maxLineSpan) }, contentType = "heading") {
                SectionHeading(
                    "Synced",
                    note = "Collected from Spotify, Deezer and YouTube. Tracks you add here play after their own.",
                )
            }
        }
        items(synced, key = { it.id }, contentType = { "playlist" }) { playlist ->
            PlaylistCard(playlist, coverUrl) { onOpenPlaylist(playlist) }
        }
    }
}

private val GRID_EDGE = 16.dp

@Composable
private fun SectionHeading(title: String, note: String?) {
    val colors = MhTheme.colors
    Column(modifier = Modifier.fillMaxWidth().padding(top = 4.dp)) {
        Text(
            title,
            style = MaterialTheme.typography.titleLarge,
            fontWeight = FontWeight.SemiBold,
            color = colors.foreground,
            modifier = Modifier.semantics { heading() },
        )
        if (note != null) {
            Text(note, style = MaterialTheme.typography.bodySmall, color = colors.mutedForeground)
        }
    }
}

/** A playlist tile: square artwork, the name, then `Spotify · 42 tracks`. */
@Composable
private fun PlaylistCard(playlist: Playlist, coverUrl: (Track, Int) -> String?, onClick: () -> Unit) {
    val colors = MhTheme.colors
    Column(modifier = Modifier.clickable(onClickLabel = "Open playlist", role = Role.Button, onClick = onClick)) {
        PlaylistArtwork(
            playlist = playlist,
            coverUrl = coverUrl,
            size = 400,
            modifier = Modifier.fillMaxWidth().aspectRatio(1f),
            shape = RoundedCornerShape(8.dp),
        )
        Spacer(Modifier.height(8.dp))
        Text(
            text = playlist.name,
            style = MaterialTheme.typography.bodyMedium,
            fontWeight = FontWeight.SemiBold,
            color = colors.foreground,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
        Text(
            text = playlistSubtitle(playlist.source, playlist.tracks.size),
            style = MaterialTheme.typography.bodySmall,
            color = colors.mutedForeground,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
    }
}

/**
 * A playlist's artwork, the web's `PlaylistCover`: the remote playlist's own image when it follows
 * one, else a mosaic of the first four albums on it, else the first track's cover, else a tinted
 * tile with the playlist's initials.
 */
@Composable
fun PlaylistArtwork(
    playlist: Playlist,
    coverUrl: (Track, Int) -> String?,
    size: Int,
    modifier: Modifier = Modifier,
    shape: Shape = RoundedCornerShape(8.dp),
) {
    val imageUrl = playlist.source?.imageUrl
    val tiles = remember(playlist.tracks, imageUrl) {
        if (imageUrl != null) emptyList()
        else playlist.tracks.filter { it.hasCover }.distinctBy { "${it.albumArtist.lowercase()}::${it.album.lowercase()}" }.take(4)
    }
    if (imageUrl == null && tiles.size == 4) {
        Column(modifier = modifier.clip(shape)) {
            for (row in tiles.chunked(2)) {
                Row(modifier = Modifier.weight(1f).fillMaxWidth()) {
                    for (track in row) {
                        Artwork(
                            url = coverUrl(track, size / 2),
                            artist = track.albumArtist,
                            title = track.album,
                            modifier = Modifier.weight(1f).fillMaxSize(),
                            shape = RectangleShape,
                        )
                    }
                }
            }
        }
    } else {
        Artwork(
            url = imageUrl ?: tiles.firstOrNull()?.let { coverUrl(it, size) },
            artist = playlist.name,
            title = playlist.name,
            modifier = modifier,
            shape = shape,
        )
    }
}
