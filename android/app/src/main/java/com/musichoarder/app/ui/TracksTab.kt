package com.musichoarder.app.ui

import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListState
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.musichoarder.app.data.NowPlayingLinks
import com.musichoarder.app.data.Track

/**
 * The Tracks list: the page's [header], Play / Shuffle, then the rows — all one scrolling list, so
 * everything above the first track scrolls away with it.
 *
 * The filter chips are not a row here any more: as on the web's phone page they are checkable
 * items with counts in the header's "Sort and filter" menu, and a pressed one shows as a removable
 * token under the search field (the header draws those). At rest the header is title, meta and
 * search — the decluttered header both clients now share.
 *
 * Empty results render *inside* the list, under the header: the search box and the tokens are how
 * you undo whatever emptied it, so they must not disappear with the rows.
 */
@Composable
fun TracksTab(
    tracks: List<Track>,
    likedIds: (Track) -> Boolean,
    playingTrackId: Int?,
    isPlayingNow: Boolean,
    coverUrl: (Track, Int) -> String?,
    onToggleLike: (Track) -> Unit,
    /** A row tap: the caller applies the tap rule (play from here, or bring the player up). */
    onActivateRow: (List<Track>, Int) -> Unit,
    contentPadding: PaddingValues,
    /** "Shared by X" per track, or null when this account owns it. */
    sharedByOf: (Track) -> String? = { null },
    /** Where a row's "Go to album / artist" lead; null for a row no album card holds. */
    linksOf: (Track) -> NowPlayingLinks? = { null },
    onOpenAlbumKey: (String) -> Unit = {},
    onOpenArtist: (String) -> Unit = {},
    modifier: Modifier = Modifier,
    /** The page header (title, meta, search), drawn as the list's first item. */
    header: @Composable () -> Unit = {},
    listState: LazyListState = rememberLazyListState(),
    /** The Play pill — the list from the top. Null leaves Play / Shuffle out. */
    onPlay: (() -> Unit)? = null,
    onShuffle: (() -> Unit)? = null,
    emptyMessage: String = "No tracks here.",
) {
    LazyColumn(state = listState, modifier = modifier, contentPadding = contentPadding) {
        item(key = "header", contentType = "header") { header() }

        if (tracks.isEmpty()) {
            item(key = "empty", contentType = "message") { ListMessage(emptyMessage) }
            return@LazyColumn
        }

        if (onPlay != null) {
            item(key = "play", contentType = "play") {
                MhPlayShufflePills(
                    onPlay = onPlay,
                    onShuffle = onShuffle,
                    modifier = Modifier.padding(bottom = 8.dp),
                )
            }
        }

        itemsIndexed(tracks, key = { _, track -> track.id }, contentType = { _, _ -> "track" }) { index, track ->
            // Both links resolve together or not at all, exactly as the player's line does.
            val links = remember(track.id, linksOf) { linksOf(track) }
            TrackRow(
                track = track,
                coverUrl = coverUrl(track, 128),
                isPlaying = track.id == playingTrackId,
                isPlayingNow = isPlayingNow,
                liked = likedIds(track),
                onToggleLike = { onToggleLike(track) },
                onClick = { onActivateRow(tracks, index) },
                sharedBy = sharedByOf(track),
                onOpenAlbum = links?.let { { onOpenAlbumKey(it.albumKey) } },
                onOpenArtist = links?.let { { onOpenArtist(it.artist) } },
            )
            if (index < tracks.lastIndex) TrackRowSeparator()
        }
    }
}
