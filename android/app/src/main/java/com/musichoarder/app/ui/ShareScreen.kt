package com.musichoarder.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListState
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Close
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
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
 * owner-only affordances stripped: no hearts (foreign ids), close instead of back, the artist as
 * plain text (there is no library to go to), and it works with no pairing at all.
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
    /** A row tap, which follows the row tap rule (see `RowTap`); [onPlay] is the Play button. */
    onActivateRow: (List<Track>, Int) -> Unit = onPlay,
) {
    val colors = MhTheme.colors
    val listState = rememberLazyListState()

    Column(modifier = modifier.fillMaxSize().background(colors.background)) {
        // The anonymous viewer has no other context, so its bar always says what this is.
        DrillInTopBar(
            title = "Shared with you",
            listState = listState,
            navigationIcon = Icons.Rounded.Close,
            navigationLabel = "Close share",
            onNavigate = onClose,
            alwaysShowTitle = true,
        )

        when (state) {
            is ShareUiState.Loading -> MessagePane("Loading share…")
            is ShareUiState.Failed ->
                if (state.gone) MessagePane(state.message) else ErrorPane(state.message, onRetry)
            is ShareUiState.Ready -> ShareTracklist(
                state = state,
                listState = listState,
                playingTrackId = playingTrackId,
                isPlayingNow = isPlayingNow,
                onPlay = onPlay,
                onActivateRow = onActivateRow,
                onShuffle = onShuffle,
                contentPadding = contentPadding,
            )
        }
    }
}

@Composable
private fun ShareTracklist(
    state: ShareUiState.Ready,
    listState: LazyListState,
    playingTrackId: Int?,
    isPlayingNow: Boolean,
    onPlay: (List<Track>, Int) -> Unit,
    onActivateRow: (List<Track>, Int) -> Unit,
    onShuffle: (List<Track>) -> Unit,
    contentPadding: PaddingValues,
) {
    val tracks = state.tracks
    val title = state.album.title ?: tracks.firstOrNull()?.title ?: "Shared music"
    val artist = state.album.artist ?: tracks.firstOrNull()?.artist ?: ""
    val cover = tracks.firstOrNull { it.hasCover }?.artworkUrl

    LazyColumn(state = listState, modifier = Modifier.fillMaxSize(), contentPadding = contentPadding) {
        item(key = "hero", contentType = "hero") {
            AlbumHero(
                coverUrl = cover,
                artist = artist,
                title = title,
                onOpenArtist = null,
                metaLines = listOfNotNull(
                    state.album.year?.toString(),
                    "Shared from ${state.link.origin.substringAfter("://")}",
                ),
                onPlay = { onPlay(tracks, 0) },
                // One song has nothing to shuffle.
                onShuffle = if (tracks.size > 1) { { onShuffle(tracks) } } else null,
            )
        }

        itemsIndexed(tracks, key = { _, track -> track.id }) { index, track ->
            TrackRow(
                track = track,
                coverUrl = null,
                isPlaying = track.id == playingTrackId,
                isPlayingNow = isPlayingNow,
                onClick = { onActivateRow(tracks, index) },
                trackNumber = track.trackNumber,
                // The cover is already the header, and hearts stay off — these ids belong to the
                // sharing server, not any library this phone can write to. With nothing to like
                // and no library to go to, the row has no menu at all.
                showArtwork = false,
            )
            if (index < tracks.lastIndex) TrackRowSeparator(showArtwork = false)
        }

        item(key = "summary", contentType = "footer") { TracklistSummary(tracks) }
    }
}
