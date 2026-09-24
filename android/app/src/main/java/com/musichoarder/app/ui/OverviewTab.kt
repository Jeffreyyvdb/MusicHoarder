package com.musichoarder.app.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListState
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.KeyboardArrowRight
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.draw.clip
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.musichoarder.app.data.Album
import com.musichoarder.app.data.ArtistGroup
import com.musichoarder.app.data.ChipKey
import com.musichoarder.app.data.LibraryTab
import com.musichoarder.app.data.NowPlayingLinks
import com.musichoarder.app.data.OverviewSections
import com.musichoarder.app.data.Track
import com.musichoarder.app.ui.theme.MhTheme

/**
 * The Overview page: your favourites, then shelves of albums and artists.
 *
 * The last four shelves are shuffled against a per-visit seed rather than genuinely at random, so
 * they stay put while the library quietly refetches but are different the next time you open the
 * app. Every section disappears entirely when it has nothing in it, as on the web.
 */
@Composable
fun OverviewTab(
    sections: OverviewSections,
    coverUrl: (Track, Int) -> String?,
    artistImageUrl: (String) -> String,
    playingTrackId: Int?,
    /** A favourite row's tap; the caller applies the row tap rule (see `RowTap`). */
    onActivateRow: (List<Track>, Int) -> Unit,
    onOpenAlbum: (Album) -> Unit,
    onOpenArtist: (ArtistGroup) -> Unit,
    onOpenTab: (LibraryTab, ChipKey?) -> Unit,
    contentPadding: PaddingValues,
    modifier: Modifier = Modifier,
    isPlayingNow: Boolean = false,
    /** A favourite row's "Remove from favourites"; null leaves the rows without a menu entry. */
    onToggleLike: ((Track) -> Unit)? = null,
    linksOf: (Track) -> NowPlayingLinks? = { null },
    onOpenAlbumKey: (String) -> Unit = {},
    onOpenArtistName: (String) -> Unit = {},
    /** "Shared by X" per track, or null when this account owns it. */
    sharedByOf: (Track) -> String? = { null },
    /** The page header (the greeting and the library's totals), drawn as the list's first item. */
    header: @Composable () -> Unit = {},
    listState: LazyListState = rememberLazyListState(),
) {
    LazyColumn(
        state = listState,
        modifier = modifier,
        contentPadding = PaddingValues(bottom = 20.dp + contentPadding.calculateBottomPadding()),
        verticalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        item(key = "header", contentType = "header") { header() }

        // The first shelf heading sits closer to the page header than one shelf does to the next —
        // the header's own padding already separates them — whichever shelf that turns out to be,
        // since an empty one is left out.
        var leading = true
        val headingTop: () -> Dp = { (if (leading) FIRST_HEADING_TOP else HEADING_TOP).also { leading = false } }

        if (sections.favouriteTracks.isNotEmpty()) {
            val top = headingTop()
            item(key = "favourites-header") {
                SectionHeader("Favourite tracks", top) {
                    onOpenTab(LibraryTab.Tracks, ChipKey.MhLiked)
                }
            }
            // ONE list, the same rows as Tracks (the web's favourites shelf became its TrackList
            // rows too), so a tap here obeys the same rule and offers the same menu.
            item(key = "favourites") {
                val favourites = sections.favouriteTracks
                Column {
                    favourites.forEachIndexed { index, track ->
                        val links = remember(track.id, linksOf) { linksOf(track) }
                        TrackRow(
                            track = track,
                            coverUrl = coverUrl(track, 128),
                            isPlaying = track.id == playingTrackId,
                            isPlayingNow = isPlayingNow,
                            // Every row here is a like — that is what put it on this shelf — so
                            // the menu offers "Remove from favourites" but no row carries a heart
                            // (one on every row said nothing; the web's shelf dropped it too).
                            liked = true,
                            showLikedMark = false,
                            onToggleLike = onToggleLike?.let { toggle -> { toggle(track) } },
                            onClick = { onActivateRow(favourites, index) },
                            sharedBy = sharedByOf(track),
                            onOpenAlbum = links?.let { { onOpenAlbumKey(it.albumKey) } },
                            onOpenArtist = links?.let { { onOpenArtistName(it.artist) } },
                        )
                        if (index < favourites.lastIndex) TrackRowSeparator()
                    }
                }
            }
        }

        albumShelf("Recently added", sections.recentAlbums, coverUrl, onOpenAlbum, headingTop) {
            onOpenTab(LibraryTab.Albums, null)
        }
        albumShelf("Last played", sections.lastPlayedAlbums, coverUrl, onOpenAlbum, headingTop) {
            onOpenTab(LibraryTab.Albums, null)
        }
        albumShelf("New to you", sections.newToYouAlbums, coverUrl, onOpenAlbum, headingTop) {
            onOpenTab(LibraryTab.Albums, null)
        }
        albumShelf("Discover — never played", sections.discoverAlbums, coverUrl, onOpenAlbum, headingTop) {
            onOpenTab(LibraryTab.Albums, null)
        }

        if (sections.artistsToRevisit.isNotEmpty()) {
            val top = headingTop()
            item(key = "artists-header") {
                SectionHeader("Artists to revisit", top) {
                    onOpenTab(LibraryTab.Artists, null)
                }
            }
            item(key = "artists-shelf") {
                LazyRow(
                    contentPadding = PaddingValues(horizontal = 16.dp, vertical = 10.dp),
                    horizontalArrangement = Arrangement.spacedBy(16.dp),
                ) {
                    items(sections.artistsToRevisit, key = { it.key }) { group ->
                        ArtistShelfTile(
                            group = group,
                            portraitUrl = artistImageUrl(group.label),
                            coverUrl = group.coverTrack?.let { coverUrl(it, 256) },
                            onClick = { onOpenArtist(group) },
                        )
                    }
                }
            }
        }

        albumShelf("From the shelves", sections.shelfAlbums, coverUrl, onOpenAlbum, headingTop) {
            onOpenTab(LibraryTab.Albums, null)
        }
    }
}

private fun LazyListScope.albumShelf(
    title: String,
    albums: List<Album>,
    coverUrl: (Track, Int) -> String?,
    onOpenAlbum: (Album) -> Unit,
    headingTop: () -> Dp,
    onOpenSection: () -> Unit,
) {
    if (albums.isEmpty()) return
    val top = headingTop()
    item(key = "$title-header") { SectionHeader(title, top, onOpenSection) }
    item(key = "$title-shelf") {
        LazyRow(
            contentPadding = PaddingValues(horizontal = 16.dp, vertical = 10.dp),
            horizontalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            items(albums, key = { it.key }) { album ->
                AlbumShelfTile(
                    album = album,
                    coverUrl = album.tracks.firstOrNull { it.hasCover }?.let { coverUrl(it, 400) },
                    onClick = { onOpenAlbum(album) },
                )
            }
        }
    }
}

/**
 * A shelf's header: the title as a heading (`text-title-2`), and a tint "See all ›" where the shelf
 * has somewhere to go — the web's Overview header. "See all" is a resting, 48dp-tall text button,
 * not a hover reveal: there is no hover on a phone.
 */
@Composable
private fun SectionHeader(title: String, top: Dp, onSeeAll: () -> Unit) {
    val colors = MhTheme.colors
    Row(
        modifier = Modifier.fillMaxWidth().padding(start = 16.dp, end = 8.dp, top = top),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            title,
            style = MaterialTheme.typography.headlineMedium,
            color = colors.foreground,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.weight(1f).semantics { heading() },
        )
        Row(
            modifier = Modifier
                .heightIn(min = 48.dp)
                .clip(RoundedCornerShape(8.dp))
                .clickable(onClickLabel = "See all ${title.lowercase()}", onClick = onSeeAll)
                .padding(start = 8.dp, end = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text("See all", style = MaterialTheme.typography.bodyLarge, color = colors.primary)
            Icon(
                Icons.AutoMirrored.Rounded.KeyboardArrowRight,
                contentDescription = null,
                tint = colors.primary,
                modifier = Modifier.size(20.dp),
            )
        }
    }
}

private val HEADING_TOP: Dp = 20.dp
private val FIRST_HEADING_TOP: Dp = 4.dp

/** A 136dp album tile, the width the web's shelves use on a phone. */
@Composable
private fun AlbumShelfTile(album: Album, coverUrl: String?, onClick: () -> Unit) {
    val colors = MhTheme.colors
    Column(
        modifier = Modifier
            .width(136.dp)
            .clickable(onClickLabel = "Open album", role = Role.Button, onClick = onClick),
    ) {
        Artwork(
            url = coverUrl,
            artist = album.artist,
            title = album.name,
            modifier = Modifier.fillMaxWidth().aspectRatio(1f),
            shape = RoundedCornerShape(8.dp),
        )
        Spacer(Modifier.height(8.dp))
        Text(
            album.name,
            style = MaterialTheme.typography.bodyMedium,
            fontWeight = FontWeight.Medium,
            color = colors.foreground,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
        Text(
            buildString {
                append(album.artist)
                album.year?.let { append(" · ").append(it) }
            },
            style = MaterialTheme.typography.bodySmall,
            color = colors.mutedForeground,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
    }
}

/** A 120dp circular artist tile. */
@Composable
private fun ArtistShelfTile(
    group: ArtistGroup,
    portraitUrl: String,
    coverUrl: String?,
    onClick: () -> Unit,
) {
    val colors = MhTheme.colors
    Column(
        modifier = Modifier
            .width(120.dp)
            .clickable(onClickLabel = "Open artist", role = Role.Button, onClick = onClick),
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
            group.label,
            style = MaterialTheme.typography.bodyMedium,
            fontWeight = FontWeight.Medium,
            color = colors.foreground,
            textAlign = TextAlign.Center,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
        Text(
            "${group.albumCount} album${if (group.albumCount == 1) "" else "s"}",
            style = MaterialTheme.typography.bodySmall,
            color = colors.mutedForeground,
            textAlign = TextAlign.Center,
            maxLines = 1,
        )
    }
}
