package com.musichoarder.app.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material.icons.rounded.PlayArrow
import androidx.compose.material.icons.rounded.Shuffle
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.musichoarder.app.data.Album
import com.musichoarder.app.data.Track

/** One album, in track order — the view that makes a record feel like a record. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AlbumScreen(
    album: Album,
    coverUrl: (Track, Int) -> String?,
    playingTrackId: Int?,
    onPlay: (List<Track>, Int) -> Unit,
    onShuffle: (List<Track>) -> Unit,
    onBack: () -> Unit,
    contentPadding: PaddingValues,
    modifier: Modifier = Modifier,
) {
    val cover = album.tracks.firstOrNull { it.hasCover }?.let { coverUrl(it, 640) }

    Scaffold(
        modifier = modifier,
        // The mini player and the system bars are handled by the root layout; a second set of
        // bottom insets here would just add dead space above it.
        contentWindowInsets = WindowInsets(0, 0, 0, 0),
        topBar = {
            TopAppBar(
                title = { Text(album.name, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "Back")
                    }
                },
            )
        },
    ) { innerPadding ->
        LazyColumn(
            modifier = Modifier.fillMaxSize().padding(innerPadding),
            contentPadding = contentPadding,
        ) {
            item {
                Column(
                    modifier = Modifier.fillMaxWidth().padding(24.dp),
                    horizontalAlignment = Alignment.CenterHorizontally,
                ) {
                    Artwork(
                        url = cover,
                        seed = album.name,
                        modifier = Modifier.size(200.dp),
                        shape = RoundedCornerShape(14.dp),
                        letterSize = 56.sp,
                    )
                    Spacer(Modifier.height(16.dp))
                    Text(
                        album.name,
                        style = MaterialTheme.typography.titleLarge,
                        fontWeight = FontWeight.SemiBold,
                        textAlign = TextAlign.Center,
                    )
                    Text(
                        album.artist,
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        textAlign = TextAlign.Center,
                    )
                    Text(
                        buildString {
                            album.year?.let { append(it).append(" · ") }
                            append(album.tracks.size)
                            append(if (album.tracks.size == 1) " track" else " tracks")
                        },
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )

                    Spacer(Modifier.height(18.dp))
                    Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                        Button(onClick = { onPlay(album.tracks, 0) }) {
                            Icon(Icons.Rounded.PlayArrow, contentDescription = null, modifier = Modifier.size(18.dp))
                            Spacer(Modifier.size(8.dp))
                            Text("Play")
                        }
                        OutlinedButton(onClick = { onShuffle(album.tracks) }) {
                            Icon(Icons.Rounded.Shuffle, contentDescription = null, modifier = Modifier.size(18.dp))
                            Spacer(Modifier.size(8.dp))
                            Text("Shuffle")
                        }
                    }
                }
                HorizontalDivider()
            }

            itemsIndexed(album.tracks, key = { _, track -> track.id }) { index, track ->
                TrackRow(
                    track = track,
                    coverUrl = null,
                    isPlaying = track.id == playingTrackId,
                    onClick = { onPlay(album.tracks, index) },
                    // The cover is already the header — repeating it 12 times adds nothing.
                    showArtwork = false,
                )
            }
        }
    }
}
