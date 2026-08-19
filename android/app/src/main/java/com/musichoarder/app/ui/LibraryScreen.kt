package com.musichoarder.app.ui

import androidx.compose.foundation.background
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
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.Logout
import androidx.compose.material.icons.rounded.Album
import androidx.compose.material.icons.rounded.LibraryMusic
import androidx.compose.material.icons.rounded.Refresh
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.musichoarder.app.data.Album
import com.musichoarder.app.data.LibraryState
import com.musichoarder.app.data.Track
import com.musichoarder.app.ui.theme.MhTheme

/**
 * The library, laid out like the web app's Listen section: a top bar whose sections are pills, a
 * page toolbar with the pill search field, then either the dense track table or the album grid.
 *
 * Tapping a row plays it *and* queues everything visible below it, so the list you are looking at is
 * the queue you get — same contract as the web player.
 */
@Composable
fun LibraryScreen(
    state: LibraryState,
    coverUrl: (Track, Int) -> String?,
    playingTrackId: Int?,
    onPlay: (List<Track>, Int) -> Unit,
    onOpenAlbum: (Album) -> Unit,
    onRefresh: () -> Unit,
    onUnpair: () -> Unit,
    contentPadding: PaddingValues,
    modifier: Modifier = Modifier,
) {
    val colors = MhTheme.colors
    var selectedTab by remember { mutableIntStateOf(0) }
    var query by remember { mutableStateOf("") }

    val tracks = remember(state.tracks, query) { state.tracks.filter { it.matches(query) } }
    val albums = remember(state.albums, query) {
        state.albums.filter { album ->
            query.isBlank() || album.name.contains(query, true) || album.artist.contains(query, true)
        }
    }

    Column(modifier = modifier.fillMaxSize().background(colors.background)) {
        // Top bar — section pills on the left, chrome buttons on the right.
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .statusBarsPadding()
                .padding(horizontal = 12.dp, vertical = 6.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            MhTabPill("Songs", selectedTab == 0) { selectedTab = 0 }
            Spacer(Modifier.size(4.dp))
            MhTabPill("Albums", selectedTab == 1) { selectedTab = 1 }
            Spacer(Modifier.weight(1f))
            MhIconButton(Icons.Rounded.Refresh, "Refresh library", onRefresh)
            Spacer(Modifier.size(8.dp))
            MhIconButton(Icons.AutoMirrored.Rounded.Logout, "Unpair this device", onUnpair)
        }
        HorizontalDivider(color = colors.border)

        MhPageToolbar(
            icon = if (selectedTab == 0) Icons.Rounded.LibraryMusic else Icons.Rounded.Album,
            title = if (selectedTab == 0) "All tracks" else "Albums",
        ) {
            MhSearchField(
                value = query,
                onValueChange = { query = it },
                placeholder = "Search artists, albums",
                modifier = Modifier.weight(1f),
            )
        }
        HorizontalDivider(color = colors.border)

        when {
            state.isLoading && state.isEmpty -> CenteredPane { CircularProgressIndicator(color = colors.primary) }
            state.error != null && state.isEmpty -> ErrorPane(state.error, onRefresh)
            state.isEmpty -> MessagePane(
                "No built tracks yet.\nThe pipeline lists songs here once it has copied them into " +
                    "the destination library."
            )
            selectedTab == 0 && tracks.isEmpty() -> MessagePane("No songs match \"$query\".")
            selectedTab == 1 && albums.isEmpty() -> MessagePane("No albums match \"$query\".")

            selectedTab == 0 -> LazyColumn(contentPadding = contentPadding) {
                itemsIndexed(tracks, key = { _, track -> track.id }) { index, track ->
                    TrackRow(
                        track = track,
                        index = index + 1,
                        coverUrl = coverUrl(track, 128),
                        isPlaying = track.id == playingTrackId,
                        onClick = { onPlay(tracks, index) },
                    )
                    HorizontalDivider(color = colors.border)
                }
            }

            else -> LazyVerticalGrid(
                columns = GridCells.Fixed(2),
                contentPadding = PaddingValues(
                    start = 16.dp,
                    end = 16.dp,
                    top = 16.dp,
                    bottom = 16.dp + contentPadding.calculateBottomPadding(),
                ),
                horizontalArrangement = Arrangement.spacedBy(16.dp),
                verticalArrangement = Arrangement.spacedBy(22.dp),
            ) {
                items(albums, key = { it.key }) { album ->
                    AlbumCard(
                        album = album,
                        coverUrl = album.tracks.firstOrNull { it.hasCover }?.let { coverUrl(it, 400) },
                        onClick = { onOpenAlbum(album) },
                    )
                }
            }
        }
    }
}

private fun Track.matches(query: String): Boolean =
    query.isBlank() ||
        title.contains(query, true) ||
        artist.contains(query, true) ||
        album.contains(query, true)

/**
 * One row of the web's track table: a zero-padded monospace index, the cover, title over artist, and
 * the duration hard right.
 *
 * [index] is the row's position in the list rather than the tag's track number — that is what the
 * web's "All tracks" table numbers, and it stays continuous while filtering. Album screens pass the
 * real track number through [trackNumber] instead.
 */
@Composable
fun TrackRow(
    track: Track,
    coverUrl: String?,
    isPlaying: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    index: Int? = null,
    trackNumber: Int? = null,
    showArtwork: Boolean = true,
) {
    val colors = MhTheme.colors
    Row(
        modifier = modifier
            .fillMaxWidth()
            .height(56.dp)
            .clickable(onClick = onClick)
            .padding(horizontal = 14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        val leading = index?.let { "%03d".format(it) } ?: trackNumber?.toString() ?: "–"
        Text(
            text = leading,
            style = MaterialTheme.typography.bodySmall,
            fontFamily = FontFamily.Monospace,
            color = if (isPlaying) colors.primary else colors.mutedForeground,
            textAlign = TextAlign.Center,
            modifier = Modifier.width(if (index != null) 34.dp else 24.dp),
        )

        if (showArtwork) {
            Spacer(Modifier.size(10.dp))
            Artwork(
                url = coverUrl,
                artist = track.albumArtist,
                title = track.album,
                modifier = Modifier.size(40.dp),
            )
        }
        Spacer(Modifier.size(12.dp))

        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = track.title,
                style = MaterialTheme.typography.bodyLarge,
                fontWeight = FontWeight.Medium,
                color = if (isPlaying) colors.primary else colors.foreground,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            Spacer(Modifier.height(2.dp))
            Text(
                text = track.artist,
                style = MaterialTheme.typography.bodySmall,
                color = colors.mutedForeground,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }

        track.durationMs?.let {
            Spacer(Modifier.size(10.dp))
            Text(
                text = formatDuration(it),
                style = MaterialTheme.typography.bodySmall,
                fontFamily = FontFamily.Monospace,
                color = colors.mutedForeground,
            )
        }
    }
}

/** Album grid tile: square cover, title, then `Artist · Year` — the web's LibraryAlbumsGrid. */
@Composable
private fun AlbumCard(album: Album, coverUrl: String?, onClick: () -> Unit) {
    val colors = MhTheme.colors
    Column(modifier = Modifier.clickable(onClick = onClick)) {
        Artwork(
            url = coverUrl,
            artist = album.artist,
            title = album.name,
            modifier = Modifier.fillMaxWidth().aspectRatio(1f),
            shape = RoundedCornerShape(10.dp),
        )
        Spacer(Modifier.height(10.dp))
        Text(
            text = album.name,
            style = MaterialTheme.typography.bodyLarge,
            fontWeight = FontWeight.Medium,
            color = colors.foreground,
            maxLines = 2,
            overflow = TextOverflow.Ellipsis,
        )
        Spacer(Modifier.height(2.dp))
        Text(
            text = buildString {
                append(album.artist)
                album.year?.let { append(" · ").append(it) }
            },
            style = MaterialTheme.typography.bodySmall,
            color = colors.mutedForeground,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
    }
}

@Composable
private fun CenteredPane(content: @Composable () -> Unit) {
    Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { content() }
}

@Composable
private fun MessagePane(message: String) {
    CenteredPane {
        Text(
            message,
            style = MaterialTheme.typography.bodyMedium,
            color = MhTheme.colors.mutedForeground,
            textAlign = TextAlign.Center,
            modifier = Modifier.padding(horizontal = 32.dp),
        )
    }
}

@Composable
private fun ErrorPane(message: String, onRetry: () -> Unit) {
    val colors = MhTheme.colors
    CenteredPane {
        Column(
            horizontalAlignment = Alignment.CenterHorizontally,
            modifier = Modifier
                .padding(28.dp)
                .background(colors.card, RoundedCornerShape(12.dp))
                .padding(20.dp),
        ) {
            Text(
                message,
                style = MaterialTheme.typography.bodyMedium,
                color = colors.foreground,
                textAlign = TextAlign.Center,
            )
            Spacer(Modifier.height(8.dp))
            TextButton(onClick = onRetry) {
                Text("Try again", color = colors.primary)
            }
        }
    }
}
