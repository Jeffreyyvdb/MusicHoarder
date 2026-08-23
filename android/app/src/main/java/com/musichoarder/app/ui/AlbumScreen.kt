package com.musichoarder.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material.icons.rounded.PlayArrow
import androidx.compose.material.icons.rounded.Shuffle
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.musichoarder.app.data.Album
import com.musichoarder.app.data.Track
import com.musichoarder.app.data.likedNow
import com.musichoarder.app.ui.theme.MhTheme

/** One album, in track order — the phone's take on the web album page's hero + tracklist. */
@Composable
fun AlbumScreen(
    album: Album,
    coverUrl: (Track, Int) -> String?,
    playingTrackId: Int?,
    likes: Map<Int, String?>,
    onToggleLike: (Track) -> Unit,
    onPlay: (List<Track>, Int) -> Unit,
    onShuffle: (List<Track>) -> Unit,
    onBack: () -> Unit,
    contentPadding: PaddingValues,
    modifier: Modifier = Modifier,
) {
    val colors = MhTheme.colors
    val cover = album.tracks.firstOrNull { it.hasCover }?.let { coverUrl(it, 640) }

    Column(modifier = modifier.fillMaxSize().background(colors.background)) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .statusBarsPadding()
                .padding(horizontal = 12.dp, vertical = 6.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            MhIconButton(Icons.AutoMirrored.Rounded.ArrowBack, "Back", onBack)
            Spacer(Modifier.size(12.dp))
            Text(
                album.name,
                style = MaterialTheme.typography.titleMedium,
                color = colors.foreground,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
        HorizontalDivider(color = colors.border)

        LazyColumn(modifier = Modifier.fillMaxSize(), contentPadding = contentPadding) {
            item {
                Column(
                    modifier = Modifier.fillMaxWidth().padding(24.dp),
                    horizontalAlignment = Alignment.CenterHorizontally,
                ) {
                    Artwork(
                        url = cover,
                        artist = album.artist,
                        title = album.name,
                        modifier = Modifier.size(196.dp),
                        shape = RoundedCornerShape(12.dp),
                    )
                    Spacer(Modifier.height(18.dp))
                    Text(
                        album.name,
                        style = MaterialTheme.typography.headlineSmall,
                        color = colors.foreground,
                        textAlign = TextAlign.Center,
                    )
                    Spacer(Modifier.height(4.dp))
                    Text(
                        buildString {
                            append(album.artist)
                            album.year?.let { append(" · ").append(it) }
                            append(" · ")
                            append(album.tracks.size)
                            append(if (album.tracks.size == 1) " track" else " tracks")
                        },
                        style = MaterialTheme.typography.bodySmall,
                        color = colors.mutedForeground,
                        textAlign = TextAlign.Center,
                    )
                    if (album.folderKeys.size > 1) {
                        // This card folds several destination folders together - say so, rather
                        // than silently hiding that the album is split on disk.
                        Text(
                            "${album.folderKeys.size} editions",
                            style = MaterialTheme.typography.labelSmall,
                            color = colors.mutedForeground.copy(alpha = 0.8f),
                            textAlign = TextAlign.Center,
                        )
                    }

                    Spacer(Modifier.height(20.dp))
                    Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                        PillButton(
                            label = "Play",
                            icon = Icons.Rounded.PlayArrow,
                            filled = true,
                            onClick = { onPlay(album.tracks, 0) },
                        )
                        PillButton(
                            label = "Shuffle",
                            icon = Icons.Rounded.Shuffle,
                            filled = false,
                            onClick = { onShuffle(album.tracks) },
                        )
                    }
                }
                HorizontalDivider(color = colors.border)
            }

            itemsIndexed(album.tracks, key = { _, track -> track.id }) { index, track ->
                TrackRow(
                    track = track,
                    coverUrl = null,
                    isPlaying = track.id == playingTrackId,
                    liked = likedNow(likes, track),
                    onToggleLike = { onToggleLike(track) },
                    onClick = { onPlay(album.tracks, index) },
                    trackNumber = track.trackNumber,
                    // The cover is already the header — repeating it 12 times adds nothing.
                    showArtwork = false,
                )
                HorizontalDivider(color = colors.border)
            }
        }
    }
}

/** The web's primary / outline button pair, rounded-full. */
@Composable
private fun PillButton(
    label: String,
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    filled: Boolean,
    onClick: () -> Unit,
) {
    val colors = MhTheme.colors
    Row(
        modifier = Modifier
            .clip(CircleShape)
            .background(if (filled) colors.primary else Color.Transparent)
            .border(1.dp, if (filled) colors.primary else colors.border, CircleShape)
            .clickable(onClick = onClick)
            .padding(horizontal = 20.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(
            icon,
            contentDescription = null,
            tint = if (filled) colors.primaryForeground else colors.foreground,
            modifier = Modifier.size(17.dp),
        )
        Spacer(Modifier.size(7.dp))
        Text(
            label,
            style = MaterialTheme.typography.labelLarge,
            fontWeight = FontWeight.SemiBold,
            color = if (filled) colors.primaryForeground else colors.foreground,
        )
    }
}
