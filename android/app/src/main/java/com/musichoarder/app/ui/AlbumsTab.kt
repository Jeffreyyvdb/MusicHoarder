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
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.musichoarder.app.data.Album
import com.musichoarder.app.data.AlbumStatus
import com.musichoarder.app.data.Track
import com.musichoarder.app.ui.theme.MhTheme

/**
 * The album grid: two columns of square cards, as the web renders at phone width.
 */
@Composable
fun AlbumsTab(
    albums: List<Album>,
    statuses: Map<String, AlbumStatus>,
    coverUrl: (Track, Int) -> String?,
    onOpenAlbum: (Album) -> Unit,
    contentPadding: PaddingValues,
    modifier: Modifier = Modifier,
) {
    LazyVerticalGrid(
        columns = GridCells.Fixed(2),
        modifier = modifier,
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
                status = statuses[album.nameKey],
                coverUrl = album.tracks.firstOrNull { it.hasCover }?.let { coverUrl(it, 400) },
                onClick = { onOpenAlbum(album) },
            )
        }
        item(span = { GridItemSpan(maxLineSpan) }) {
            Text(
                text = "${albums.size.formatGrouped()} album${if (albums.size == 1) "" else "s"}",
                style = MaterialTheme.typography.bodySmall,
                color = MhTheme.colors.mutedForeground,
                modifier = Modifier.fillMaxWidth().padding(top = 6.dp),
                textAlign = androidx.compose.ui.text.style.TextAlign.Center,
            )
        }
    }
}

/** Album grid tile: square cover, link-status dot, title, then `Artist · Year`. */
@Composable
fun AlbumCard(album: Album, status: AlbumStatus?, coverUrl: String?, onClick: () -> Unit) {
    val colors = MhTheme.colors
    Column(modifier = Modifier.clickable(onClick = onClick)) {
        Box {
            Artwork(
                url = coverUrl,
                artist = album.artist,
                title = album.name,
                modifier = Modifier.fillMaxWidth().aspectRatio(1f),
                shape = RoundedCornerShape(10.dp),
            )
            statusDot(status)?.let { dot ->
                Box(
                    modifier = Modifier
                        .padding(6.dp)
                        .size(10.dp)
                        .clip(CircleShape)
                        .background(dot)
                        .border(2.dp, Color.Black.copy(alpha = 0.35f), CircleShape),
                )
            }
        }
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
        if (album.folderKeys.size > 1) {
            // The card folds several destination folders together - say so, rather than silently
            // hiding that this album is split on disk.
            Text(
                text = "${album.folderKeys.size} editions",
                style = MaterialTheme.typography.labelSmall,
                color = colors.mutedForeground.copy(alpha = 0.8f),
                maxLines = 1,
            )
        }
    }
}

/**
 * The corner dot's colour, or null when nothing is known yet. A confirmed mis-match dominates
 * regardless of link state, which is why the verdict is checked first.
 */
@Composable
private fun statusDot(status: AlbumStatus?): Color? {
    if (status == null) return null
    val colors = MhTheme.colors
    return when {
        status.isWrong -> colors.destructive
        status.isLinked -> Color(0xFF4ADE80)
        status.isLocalOnly -> Color.White.copy(alpha = 0.7f)
        else -> null
    }
}
