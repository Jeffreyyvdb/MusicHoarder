package com.musichoarder.app.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
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
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.musichoarder.app.data.ArtistGroup
import com.musichoarder.app.data.Track
import com.musichoarder.app.ui.theme.MhTheme
import kotlinx.coroutines.launch

/**
 * The artist grid under the page's [header]: circular portraits, three columns at phone width and
 * as many as fit on anything wider.
 *
 * Deliberately still a grid, where the web's compact page became a list — the portraits are what
 * the Android page is for — but with the web's phone index: a trailing A–Z column that jumps to a
 * letter ([MhLetterIndex]), hidden under 20 artists as the web hides it. The two agree on what is
 * listed and what a tap does, which is the part that has to match.
 */
@Composable
fun ArtistsTab(
    artists: List<ArtistGroup>,
    artistImageUrl: (String) -> String,
    coverUrl: (Track, Int) -> String?,
    onOpenArtist: (ArtistGroup) -> Unit,
    contentPadding: PaddingValues,
    modifier: Modifier = Modifier,
    /** The page header (title, meta, search, A–Z), drawn as the grid's first, full-width item. */
    header: @Composable () -> Unit = {},
    gridState: LazyGridState = rememberLazyGridState(),
    /** The letters the index can jump to (the initials of what is listed). */
    presentLetters: Set<String> = emptySet(),
    emptyMessage: String = "No artists yet.",
) {
    val colors = MhTheme.colors
    val scope = rememberCoroutineScope()
    val showIndex = artists.size >= INDEX_THRESHOLD
    Box(modifier = modifier) {
    LazyVerticalGrid(
        columns = GridCells.Adaptive(minSize = 104.dp),
        state = gridState,
        modifier = Modifier.fillMaxSize(),
        contentPadding = PaddingValues(
            start = GRID_EDGE,
            // Portraits stop short of the index column rather than sitting under its letters.
            end = if (showIndex) INDEX_WIDTH else GRID_EDGE,
            bottom = 16.dp + contentPadding.calculateBottomPadding(),
        ),
        horizontalArrangement = Arrangement.spacedBy(12.dp),
        verticalArrangement = Arrangement.spacedBy(20.dp),
    ) {
        item(key = "header", span = { GridItemSpan(maxLineSpan) }, contentType = "header") {
            Box(
                modifier = Modifier.bleedHorizontally(
                    start = GRID_EDGE,
                    end = if (showIndex) INDEX_WIDTH else GRID_EDGE,
                ),
            ) { header() }
        }
        if (artists.isEmpty()) {
            item(key = "empty", span = { GridItemSpan(maxLineSpan) }, contentType = "message") {
                ListMessage(emptyMessage)
            }
            return@LazyVerticalGrid
        }
        items(artists, key = { it.key }, contentType = { "artist" }) { group ->
            ArtistCard(
                group = group,
                portraitUrl = artistImageUrl(group.label),
                coverUrl = group.coverTrack?.let { coverUrl(it, 256) },
                onClick = { onOpenArtist(group) },
            )
        }
        item(key = "count", span = { GridItemSpan(maxLineSpan) }, contentType = "footer") {
            Text(
                text = "${artists.size.formatGrouped()} artist${if (artists.size == 1) "" else "s"}",
                style = MaterialTheme.typography.bodySmall,
                color = colors.mutedForeground,
                textAlign = TextAlign.Center,
                modifier = Modifier.fillMaxWidth().padding(top = 6.dp),
            )
        }
    }
    if (showIndex) {
        MhLetterIndex(
            present = presentLetters,
            onJump = { letter ->
                val at = artists.indexOfFirst { it.initial == letter }
                // +1: the header is the grid's first item.
                if (at >= 0) scope.launch { gridState.scrollToItem(at + 1) }
            },
            modifier = Modifier
                .align(Alignment.CenterEnd)
                // Below the header's title row and search field (so it never covers the field's
                // Clear), and clear of the player / navigation bar below.
                .padding(top = INDEX_TOP, bottom = contentPadding.calculateBottomPadding()),
        )
    }
    }
}

private val GRID_EDGE = 12.dp

/** The index column's width: Material's 48dp touch floor (the web's 44pt column). */
private val INDEX_WIDTH = 48.dp

/** Where the index starts: under the header's title row (~72dp) and search field (48dp). */
private val INDEX_TOP = 136.dp

/** Below this many artists the index is more chrome than it is worth (the web's threshold). */
private const val INDEX_THRESHOLD = 20

/**
 * A circular portrait captioned `N albums · M tracks`.
 *
 * The portrait endpoint 404s for anyone the providers do not know, so an album cover sits underneath
 * it and shows through when nothing paints - the two-stage fallback the web's `Cover` does with
 * `fallbackUrl`.
 */
@Composable
private fun ArtistCard(
    group: ArtistGroup,
    portraitUrl: String,
    coverUrl: String?,
    onClick: () -> Unit,
) {
    val colors = MhTheme.colors
    Column(
        // A named action for TalkBack ("Double-tap to open artist"), not a bare "activate".
        modifier = Modifier.clickable(onClickLabel = "Open artist", role = Role.Button, onClick = onClick),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Artwork(
            url = portraitUrl,
            fallbackUrl = coverUrl,
            artist = group.label,
            title = group.label,
            modifier = Modifier.fillMaxWidth().aspectRatio(1f),
            shape = CircleShape,
        )
        Spacer(Modifier.height(8.dp))
        Text(
            text = group.label,
            style = MaterialTheme.typography.bodyMedium,
            fontWeight = FontWeight.SemiBold,
            color = colors.foreground,
            textAlign = TextAlign.Center,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
        Text(
            text = "${group.albumCount} album${if (group.albumCount == 1) "" else "s"} · " +
                "${group.trackCount} track${if (group.trackCount == 1) "" else "s"}",
            style = MaterialTheme.typography.bodySmall,
            color = colors.mutedForeground,
            textAlign = TextAlign.Center,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
    }
}
