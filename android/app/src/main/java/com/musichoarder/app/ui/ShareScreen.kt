package com.musichoarder.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
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
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Close
import androidx.compose.material.icons.rounded.PlayArrow
import androidx.compose.material.icons.rounded.Shuffle
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.musichoarder.app.data.ShareAlbum
import com.musichoarder.app.data.ShareLink
import com.musichoarder.app.data.Track
import com.musichoarder.app.ui.theme.MhTheme

/**
 * What the anonymous share viewer is showing. Every state carries the [link] so retry and the
 * per-song URL builders never have to re-parse anything.
 */
sealed interface ShareUiState {
    val link: ShareLink

    data class Loading(override val link: ShareLink) : ShareUiState

    data class Ready(
        override val link: ShareLink,
        val album: ShareAlbum,
        /** "Song" | "Album" — the anonymous surface has no wider scopes. */
        val scope: String,
        val tracks: List<Track>,
    ) : ShareUiState

    data class Failed(
        override val link: ShareLink,
        val message: String,
        /** True when the link itself is dead (revoked/unknown) — retrying cannot help. */
        val gone: Boolean,
    ) : ShareUiState
}

/**
 * The share viewer — an https share link opened in the app. The [AlbumScreen] layout with the
 * owner-only affordances stripped: no hearts (foreign ids), close instead of back, and it works
 * with no pairing at all.
 */
@Composable
fun ShareScreen(
    state: ShareUiState,
    playingTrackId: Int?,
    isPlayingNow: Boolean,
    onPlay: (List<Track>, Int) -> Unit,
    onShuffle: (List<Track>) -> Unit,
    onClose: () -> Unit,
    onRetry: () -> Unit,
    contentPadding: PaddingValues,
    modifier: Modifier = Modifier,
) {
    val colors = MhTheme.colors

    Column(modifier = modifier.fillMaxSize().background(colors.background)) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .statusBarsPadding()
                .padding(horizontal = 12.dp, vertical = 6.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            MhIconButton(Icons.Rounded.Close, "Close share", onClose)
            Spacer(Modifier.size(12.dp))
            Text(
                "Shared with you",
                style = MaterialTheme.typography.titleMedium,
                color = colors.foreground,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
        HorizontalDivider(color = colors.border)

        when (state) {
            is ShareUiState.Loading -> MessagePane("Loading share…")
            is ShareUiState.Failed ->
                if (state.gone) MessagePane(state.message) else ErrorPane(state.message, onRetry)
            is ShareUiState.Ready -> ShareTracklist(
                state = state,
                playingTrackId = playingTrackId,
                isPlayingNow = isPlayingNow,
                onPlay = onPlay,
                onShuffle = onShuffle,
                contentPadding = contentPadding,
            )
        }
    }
}

@Composable
private fun ShareTracklist(
    state: ShareUiState.Ready,
    playingTrackId: Int?,
    isPlayingNow: Boolean,
    onPlay: (List<Track>, Int) -> Unit,
    onShuffle: (List<Track>) -> Unit,
    contentPadding: PaddingValues,
) {
    val colors = MhTheme.colors
    val tracks = state.tracks
    val title = state.album.title ?: tracks.firstOrNull()?.title ?: "Shared music"
    val artist = state.album.artist ?: tracks.firstOrNull()?.artist ?: ""
    val cover = tracks.firstOrNull { it.hasCover }?.artworkUrl

    LazyColumn(modifier = Modifier.fillMaxSize(), contentPadding = contentPadding) {
        item {
            Column(
                modifier = Modifier.fillMaxWidth().padding(24.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                Artwork(
                    url = cover,
                    artist = artist,
                    title = title,
                    modifier = Modifier.size(196.dp),
                    shape = RoundedCornerShape(12.dp),
                )
                Spacer(Modifier.height(18.dp))
                Text(
                    title,
                    style = MaterialTheme.typography.headlineSmall,
                    color = colors.foreground,
                    textAlign = TextAlign.Center,
                )
                Spacer(Modifier.height(4.dp))
                Text(
                    buildString {
                        append(artist)
                        state.album.year?.let { append(" · ").append(it) }
                        append(" · ")
                        append(tracks.size)
                        append(if (tracks.size == 1) " track" else " tracks")
                    },
                    style = MaterialTheme.typography.bodySmall,
                    color = colors.mutedForeground,
                    textAlign = TextAlign.Center,
                )
                Text(
                    "Shared from ${state.link.origin.substringAfter("://")}",
                    style = MaterialTheme.typography.labelSmall,
                    color = colors.mutedForeground.copy(alpha = 0.8f),
                    textAlign = TextAlign.Center,
                )

                Spacer(Modifier.height(20.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    PillButton(
                        label = "Play",
                        icon = Icons.Rounded.PlayArrow,
                        filled = true,
                        onClick = { onPlay(tracks, 0) },
                    )
                    if (tracks.size > 1) {
                        PillButton(
                            label = "Shuffle",
                            icon = Icons.Rounded.Shuffle,
                            filled = false,
                            onClick = { onShuffle(tracks) },
                        )
                    }
                }
            }
            HorizontalDivider(color = colors.border)
        }

        itemsIndexed(tracks, key = { _, track -> track.id }) { index, track ->
            TrackRow(
                track = track,
                coverUrl = null,
                isPlaying = track.id == playingTrackId,
                isPlayingNow = isPlayingNow,
                onClick = { onPlay(tracks, index) },
                trackNumber = track.trackNumber,
                // The cover is already the header, and hearts stay off — these ids belong to the
                // sharing server, not any library this phone can write to.
                showArtwork = false,
            )
            HorizontalDivider(color = colors.border)
        }
    }
}
