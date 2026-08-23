package com.musichoarder.app.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Description
import androidx.compose.material.icons.rounded.Favorite
import androidx.compose.material.icons.rounded.Link
import androidx.compose.material.icons.rounded.MusicNote
import androidx.compose.material.icons.rounded.Sd
import androidx.compose.material.icons.rounded.Videocam
import androidx.compose.material.icons.rounded.AutoAwesome
import androidx.compose.material3.HorizontalDivider
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.unit.dp
import com.musichoarder.app.data.CHIP_KEYS
import com.musichoarder.app.data.CHIP_LABELS
import com.musichoarder.app.data.ChipKey
import com.musichoarder.app.data.Track
import com.musichoarder.app.ui.theme.MhTheme

/**
 * The Tracks list: the filter chips over the numbered rows.
 *
 * The chip row scrolls sideways rather than wrapping the way the web does - seven chips wrapped at
 * phone width is a three-line block eating a third of the first screen.
 */
@Composable
fun TracksTab(
    tracks: List<Track>,
    chips: Set<ChipKey>,
    chipCounts: Map<ChipKey, Int>,
    likedIds: (Track) -> Boolean,
    playingTrackId: Int?,
    isPlayingNow: Boolean,
    coverUrl: (Track, Int) -> String?,
    onToggleChip: (ChipKey) -> Unit,
    onToggleLike: (Track) -> Unit,
    onPlay: (List<Track>, Int) -> Unit,
    contentPadding: PaddingValues,
    modifier: Modifier = Modifier,
) {
    val colors = MhTheme.colors
    Column(modifier = modifier) {
        LazyRow(
            contentPadding = PaddingValues(horizontal = 12.dp, vertical = 8.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            items(CHIP_KEYS, key = { it.name }) { key ->
                MhFilterChip(
                    label = CHIP_LABELS.getValue(key),
                    pressed = key in chips,
                    onClick = { onToggleChip(key) },
                    icon = chipIcon(key),
                    count = chipCounts[key],
                )
            }
        }
        HorizontalDivider(color = colors.border)

        if (tracks.isEmpty()) {
            // The web's copy, because it explains the one thing that is not obvious: every chip has
            // to match, and each count tells you which one is the dead end.
            MessagePane(
                "No tracks match these filters.\n\nEvery active chip has to match. Each chip's " +
                    "number is what you would be left with if you pressed it, so a zero shows you " +
                    "which one is the dead end."
            )
            return@Column
        }

        LazyColumn(contentPadding = contentPadding) {
            itemsIndexed(tracks, key = { _, track -> track.id }) { index, track ->
                TrackRow(
                    track = track,
                    index = index + 1,
                    coverUrl = coverUrl(track, 128),
                    isPlaying = track.id == playingTrackId,
                    isPlayingNow = isPlayingNow,
                    liked = likedIds(track),
                    onToggleLike = { onToggleLike(track) },
                    onClick = { onPlay(tracks, index) },
                )
                HorizontalDivider(color = colors.border, modifier = Modifier.padding(start = 14.dp))
            }
        }
    }
}

private fun chipIcon(key: ChipKey): ImageVector = when (key) {
    ChipKey.SpotifyLiked -> Icons.Rounded.MusicNote
    ChipKey.MhLiked -> Icons.Rounded.Favorite
    ChipKey.Local -> Icons.Rounded.Sd
    ChipKey.Added -> Icons.Rounded.Link
    ChipKey.Video -> Icons.Rounded.Videocam
    ChipKey.Lyrics -> Icons.Rounded.Description
    ChipKey.Unreleased -> Icons.Rounded.AutoAwesome
}
