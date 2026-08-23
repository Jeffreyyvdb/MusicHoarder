package com.musichoarder.app.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.musichoarder.app.data.ArtistGroup
import com.musichoarder.app.data.ArtistMode
import com.musichoarder.app.data.Track
import com.musichoarder.app.ui.theme.MhTheme

/**
 * The artist grid: the A-Z index and the Primary/All toggle above three columns of circular
 * portraits.
 */
@Composable
fun ArtistsTab(
    artists: List<ArtistGroup>,
    presentLetters: Set<String>,
    letter: String?,
    mode: ArtistMode,
    artistImageUrl: (String) -> String,
    coverUrl: (Track, Int) -> String?,
    onSelectLetter: (String?) -> Unit,
    onSelectMode: (ArtistMode) -> Unit,
    onOpenArtist: (ArtistGroup) -> Unit,
    contentPadding: PaddingValues,
    modifier: Modifier = Modifier,
) {
    val colors = MhTheme.colors
    Column(modifier = modifier) {
        Column(modifier = Modifier.padding(horizontal = 12.dp, vertical = 8.dp)) {
            MhAlphabetBar(selected = letter, present = presentLetters, onSelect = onSelectLetter)
            Spacer(Modifier.height(8.dp))
            MhSegmented(
                options = listOf(
                    "Primary" to (mode == ArtistMode.Primary),
                    "All" to (mode == ArtistMode.All),
                ),
                onSelect = { onSelectMode(if (it == 0) ArtistMode.Primary else ArtistMode.All) },
                modifier = Modifier.align(Alignment.End),
            )
        }
        HorizontalDivider(color = colors.border)

        if (artists.isEmpty()) {
            MessagePane("No artists in this range.")
            return@Column
        }

        LazyVerticalGrid(
            columns = GridCells.Fixed(3),
            contentPadding = PaddingValues(
                start = 12.dp,
                end = 12.dp,
                top = 16.dp,
                bottom = 16.dp + contentPadding.calculateBottomPadding(),
            ),
            horizontalArrangement = Arrangement.spacedBy(12.dp),
            verticalArrangement = Arrangement.spacedBy(20.dp),
        ) {
            items(artists, key = { it.key }) { group ->
                ArtistCard(
                    group = group,
                    portraitUrl = artistImageUrl(group.label),
                    coverUrl = group.coverTrack?.let { coverUrl(it, 256) },
                    onClick = { onOpenArtist(group) },
                )
            }
            item(span = { GridItemSpan(maxLineSpan) }) {
                Text(
                    text = "${artists.size.formatGrouped()} artist${if (artists.size == 1) "" else "s"}",
                    style = MaterialTheme.typography.bodySmall,
                    color = colors.mutedForeground,
                    textAlign = TextAlign.Center,
                    modifier = Modifier.fillMaxWidth().padding(top = 6.dp),
                )
            }
        }
    }
}

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
        modifier = Modifier.clickable(onClick = onClick),
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
            style = MaterialTheme.typography.bodyLarge,
            fontWeight = FontWeight.Medium,
            color = colors.foreground,
            textAlign = TextAlign.Center,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
        Spacer(Modifier.height(2.dp))
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
