package com.musichoarder.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.Logout
import androidx.compose.material.icons.rounded.Album
import androidx.compose.material.icons.rounded.Close
import androidx.compose.material.icons.rounded.MusicNote
import androidx.compose.material.icons.rounded.Refresh
import androidx.compose.material.icons.rounded.Search
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Tab
import androidx.compose.material3.PrimaryTabRow
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.musichoarder.app.data.Album
import com.musichoarder.app.data.LibraryState
import com.musichoarder.app.data.Track

/**
 * The library, in the two shapes that matter on a phone: a flat song list and an album grid. Tapping
 * a row plays it *and* queues everything visible below it, so the list you are looking at is the
 * queue you get — same contract as the web player.
 */
@OptIn(ExperimentalMaterial3Api::class)
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
    var selectedTab by remember { mutableIntStateOf(0) }
    var query by remember { mutableStateOf("") }

    val tracks = remember(state.tracks, query) { state.tracks.filter { it.matches(query) } }
    val albums = remember(state.albums, query) {
        state.albums.filter { album ->
            query.isBlank() ||
                album.name.contains(query, true) ||
                album.artist.contains(query, true)
        }
    }

    Scaffold(
        modifier = modifier,
        // The mini player and the system bars are handled by the root layout; a second set of
        // bottom insets here would just add dead space above it.
        contentWindowInsets = WindowInsets(0, 0, 0, 0),
        topBar = {
            TopAppBar(
                title = { Text("Library") },
                actions = {
                    IconButton(onClick = onRefresh) {
                        Icon(Icons.Rounded.Refresh, contentDescription = "Refresh library")
                    }
                    IconButton(onClick = onUnpair) {
                        Icon(Icons.AutoMirrored.Rounded.Logout, contentDescription = "Unpair this device")
                    }
                },
            )
        },
    ) { innerPadding ->
        Column(modifier = Modifier.fillMaxSize().padding(innerPadding)) {
            OutlinedTextField(
                value = query,
                onValueChange = { query = it },
                placeholder = { Text("Search songs, artists, albums") },
                leadingIcon = { Icon(Icons.Rounded.Search, contentDescription = null) },
                trailingIcon = {
                    if (query.isNotEmpty()) {
                        IconButton(onClick = { query = "" }) {
                            Icon(Icons.Rounded.Close, contentDescription = "Clear search")
                        }
                    }
                },
                singleLine = true,
                modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp),
            )

            PrimaryTabRow(selectedTabIndex = selectedTab) {
                Tab(
                    selected = selectedTab == 0,
                    onClick = { selectedTab = 0 },
                    text = { Text("Songs") },
                    icon = { Icon(Icons.Rounded.MusicNote, contentDescription = null) },
                )
                Tab(
                    selected = selectedTab == 1,
                    onClick = { selectedTab = 1 },
                    text = { Text("Albums") },
                    icon = { Icon(Icons.Rounded.Album, contentDescription = null) },
                )
            }

            when {
                state.isLoading && state.isEmpty -> LoadingPane()
                state.error != null && state.isEmpty -> ErrorPane(state.error, onRefresh)
                state.isEmpty -> EmptyPane("Nothing in this library yet.")
                selectedTab == 0 && tracks.isEmpty() -> EmptyPane("No songs match \"$query\".")
                selectedTab == 1 && albums.isEmpty() -> EmptyPane("No albums match \"$query\".")
                selectedTab == 0 -> LazyColumn(contentPadding = contentPadding) {
                    itemsIndexed(tracks, key = { _, track -> track.id }) { index, track ->
                        TrackRow(
                            track = track,
                            coverUrl = coverUrl(track, 128),
                            isPlaying = track.id == playingTrackId,
                            onClick = { onPlay(tracks, index) },
                        )
                    }
                }
                else -> LazyVerticalGrid(
                    columns = GridCells.Adaptive(minSize = 148.dp),
                    contentPadding = PaddingValues(
                        start = 16.dp,
                        end = 16.dp,
                        top = 16.dp,
                        bottom = 16.dp + contentPadding.calculateBottomPadding(),
                    ),
                    horizontalArrangement = Arrangement.spacedBy(14.dp),
                    verticalArrangement = Arrangement.spacedBy(18.dp),
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
}

private fun Track.matches(query: String): Boolean =
    query.isBlank() ||
        title.contains(query, true) ||
        artist.contains(query, true) ||
        album.contains(query, true)

@Composable
fun TrackRow(
    track: Track,
    coverUrl: String?,
    isPlaying: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    showArtwork: Boolean = true,
) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .clickable(onClick = onClick)
            .padding(horizontal = 16.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (showArtwork) {
            Artwork(url = coverUrl, seed = track.album, modifier = Modifier.size(48.dp), letterSize = 18.sp)
            Spacer(Modifier.size(12.dp))
        } else {
            Box(modifier = Modifier.size(28.dp), contentAlignment = Alignment.Center) {
                Text(
                    text = track.trackNumber?.toString() ?: "–",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            Spacer(Modifier.size(12.dp))
        }

        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = track.title,
                style = MaterialTheme.typography.bodyLarge,
                fontWeight = if (isPlaying) FontWeight.SemiBold else FontWeight.Normal,
                color = if (isPlaying) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurface,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            Text(
                text = track.artist,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }

        track.durationMs?.let {
            Spacer(Modifier.size(8.dp))
            Text(
                text = formatDuration(it),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

@Composable
private fun AlbumCard(album: Album, coverUrl: String?, onClick: () -> Unit) {
    Column(modifier = Modifier.clickable(onClick = onClick)) {
        Artwork(
            url = coverUrl,
            seed = album.name,
            modifier = Modifier.fillMaxWidth().aspectRatio(1f),
            shape = RoundedCornerShape(10.dp),
            letterSize = 34.sp,
        )
        Spacer(Modifier.height(8.dp))
        Text(
            text = album.name,
            style = MaterialTheme.typography.bodyMedium,
            fontWeight = FontWeight.Medium,
            maxLines = 2,
            overflow = TextOverflow.Ellipsis,
        )
        Text(
            text = album.artist,
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
    }
}

@Composable
private fun LoadingPane() {
    Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        CircularProgressIndicator()
    }
}

@Composable
private fun EmptyPane(message: String) {
    Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Text(
            message,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

@Composable
private fun ErrorPane(message: String, onRetry: () -> Unit) {
    Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Column(
            horizontalAlignment = Alignment.CenterHorizontally,
            modifier = Modifier
                .padding(32.dp)
                .background(MaterialTheme.colorScheme.errorContainer, RoundedCornerShape(12.dp))
                .padding(20.dp),
        ) {
            Text(
                message,
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onErrorContainer,
            )
            Spacer(Modifier.height(8.dp))
            TextButton(onClick = onRetry) { Text("Try again") }
        }
    }
}
