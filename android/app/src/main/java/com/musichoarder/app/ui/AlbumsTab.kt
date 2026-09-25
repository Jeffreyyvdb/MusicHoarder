package com.musichoarder.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyGridState
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.grid.rememberLazyGridState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.layout
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.offset
import com.musichoarder.app.data.Album
import com.musichoarder.app.data.AlbumStatus
import com.musichoarder.app.data.Track
import com.musichoarder.app.ui.theme.MhTheme

/**
 * The album grid under the page's [header]: square cards, two columns at phone width (the web's
 * compact grid) and as many as fit on anything wider — a fixed two made tablet and landscape cards
 * balloon to half the screen.
 */
@Composable
fun AlbumsTab(
    albums: List<Album>,
    statuses: Map<String, AlbumStatus>,
    coverUrl: (Track, Int) -> String?,
    onOpenAlbum: (Album) -> Unit,
    contentPadding: PaddingValues,
    modifier: Modifier = Modifier,
    /** The page header (title, meta, search, tokens), drawn as the grid's first, full-width item. */
    header: @Composable () -> Unit = {},
    gridState: LazyGridState = rememberLazyGridState(),
    /** Shown under the header in place of the cards; null when there are cards to show. */
    emptyMessage: String? = null,
) {
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
        items(albums, key = { it.key }, contentType = { "album" }) { album ->
            AlbumCard(
                album = album,
                status = statuses[album.nameKey],
                coverUrl = album.tracks.firstOrNull { it.hasCover }?.let { coverUrl(it, 400) },
                onClick = { onOpenAlbum(album) },
            )
        }
        item(key = "count", span = { GridItemSpan(maxLineSpan) }, contentType = "footer") {
            Text(
                text = "${albums.size.formatGrouped()} album${if (albums.size == 1) "" else "s"}",
                style = MaterialTheme.typography.bodySmall,
                color = MhTheme.colors.mutedForeground,
                modifier = Modifier.fillMaxWidth().padding(top = 6.dp),
                textAlign = TextAlign.Center,
            )
        }
    }
}

private val GRID_EDGE = 16.dp

/**
 * Lets a full-width grid item reach the grid's own edges through its side padding, so a header
 * inside the grid lines up with the headers of the list tabs instead of sitting [edge] further in.
 * Compose does not clip a child's touches to its slot, so the bled part still takes taps.
 */
fun Modifier.bleedHorizontally(edge: Dp): Modifier = bleedHorizontally(edge, edge)

/** [bleedHorizontally] for a grid whose two side paddings differ (the artists' index column). */
fun Modifier.bleedHorizontally(start: Dp, end: Dp): Modifier = layout { measurable, constraints ->
    val extra = (start + end).roundToPx()
    val placeable = measurable.measure(constraints.offset(horizontal = extra))
    layout(placeable.width - extra, placeable.height) {
        placeable.place(-start.roundToPx(), 0)
    }
}

/** Album grid tile: square cover, an attention mark when one is due, title, then `Artist · Year`. */
@Composable
fun AlbumCard(album: Album, status: AlbumStatus?, coverUrl: String?, onClick: () -> Unit) {
    val colors = MhTheme.colors
    // A named action for TalkBack ("Double-tap to open album"); the card's text and the mark's
    // label below merge into its one announcement.
    Column(modifier = Modifier.clickable(onClickLabel = "Open album", role = Role.Button, onClick = onClick)) {
        Box {
            Artwork(
                url = coverUrl,
                artist = album.artist,
                title = album.name,
                modifier = Modifier.fillMaxWidth().aspectRatio(1f),
                shape = RoundedCornerShape(8.dp),
            )
            attentionMark(status)?.let { mark ->
                // Told apart by shape as well as colour — a filled red dot for a disputed match, a
                // hollow ring for "on no provider" — and named, so TalkBack hears it too.
                Box(
                    modifier = Modifier
                        .padding(6.dp)
                        .size(10.dp)
                        .clip(CircleShape)
                        .border(2.dp, Color.Black.copy(alpha = 0.35f), CircleShape)
                        .padding(1.dp)
                        .then(
                            if (mark == AttentionMark.Wrong) {
                                Modifier
                                    .background(colors.destructive.copy(alpha = 0.3f), CircleShape)
                                    .border(2.dp, colors.destructive, CircleShape)
                            } else {
                                // Over artwork, so white rather than a theme token (as the
                                // equalizer over art is): it has to read on any cover.
                                Modifier.border(2.dp, Color.White.copy(alpha = 0.8f), CircleShape)
                            }
                        )
                        .semantics { contentDescription = mark.label },
                )
            }
        }
        Spacer(Modifier.height(8.dp))
        Text(
            text = album.name,
            style = MaterialTheme.typography.bodyMedium,
            fontWeight = FontWeight.SemiBold,
            color = colors.foreground,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
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
        if (album.folderKeys.size > 1) {
            // The card folds several destination folders together - say so, rather than silently
            // hiding that this album is split on disk.
            Text(
                text = "${album.folderKeys.size} editions",
                style = MaterialTheme.typography.bodySmall,
                color = colors.mutedForeground,
                maxLines = 1,
            )
        }
    }
}

/** The two link states worth a mark on the cover, with the web badge's words for them. */
private enum class AttentionMark(val label: String) {
    Wrong("Likely wrong album — AI flagged the match"),
    LocalOnly("Local only — not on any provider"),
}

/**
 * The corner mark, or null for none. Only the exceptions are marked, as on the web: a normal
 * (linked, or still checking) album carries no dot — a dot on every cover said nothing. A confirmed
 * mis-match dominates regardless of link state, which is why the verdict is checked first.
 */
private fun attentionMark(status: AlbumStatus?): AttentionMark? = when {
    status == null -> null
    status.isWrong -> AttentionMark.Wrong
    status.isLocalOnly -> AttentionMark.LocalOnly
    else -> null
}
