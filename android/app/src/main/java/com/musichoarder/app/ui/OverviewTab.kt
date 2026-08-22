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
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.KeyboardArrowRight
import androidx.compose.material.icons.rounded.AutoAwesome
import androidx.compose.material.icons.rounded.Album
import androidx.compose.material.icons.rounded.Explore
import androidx.compose.material.icons.rounded.Favorite
import androidx.compose.material.icons.rounded.Group
import androidx.compose.material.icons.rounded.Schedule
import androidx.compose.material.icons.rounded.AutoFixHigh
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.musichoarder.app.data.Album
import com.musichoarder.app.data.ArtistGroup
import com.musichoarder.app.data.ChipKey
import com.musichoarder.app.data.LibraryTab
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
    onPlayTracks: (List<Track>, Int) -> Unit,
    onOpenAlbum: (Album) -> Unit,
    onOpenArtist: (ArtistGroup) -> Unit,
    onOpenTab: (LibraryTab, ChipKey?) -> Unit,
    contentPadding: PaddingValues,
    modifier: Modifier = Modifier,
) {
    LazyColumn(
        modifier = modifier,
        contentPadding = PaddingValues(
            top = 8.dp,
            bottom = 20.dp + contentPadding.calculateBottomPadding(),
        ),
        verticalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        if (sections.favouriteTracks.isNotEmpty()) {
            item(key = "favourites-header") {
                SectionHeader("Favourite tracks", Icons.Rounded.Favorite) {
                    onOpenTab(LibraryTab.Tracks, ChipKey.MhLiked)
                }
            }
            itemsIndexed(sections.favouriteTracks, key = { _, track -> track.id }) { index, track ->
                FavouriteRow(
                    track = track,
                    coverUrl = coverUrl(track, 128),
                    isPlaying = track.id == playingTrackId,
                    onClick = { onPlayTracks(sections.favouriteTracks, index) },
                )
            }
        }

        albumShelf("Recently added", Icons.Rounded.AutoAwesome, sections.recentAlbums, coverUrl, onOpenAlbum) {
            onOpenTab(LibraryTab.Albums, null)
        }
        albumShelf("Last played", Icons.Rounded.Schedule, sections.lastPlayedAlbums, coverUrl, onOpenAlbum) {
            onOpenTab(LibraryTab.Albums, null)
        }
        albumShelf("New to you", Icons.Rounded.AutoFixHigh, sections.newToYouAlbums, coverUrl, onOpenAlbum) {
            onOpenTab(LibraryTab.Albums, null)
        }
        albumShelf("Discover - never played", Icons.Rounded.Explore, sections.discoverAlbums, coverUrl, onOpenAlbum) {
            onOpenTab(LibraryTab.Albums, null)
        }

        if (sections.artistsToRevisit.isNotEmpty()) {
            item(key = "artists-header") {
                SectionHeader("Artists to revisit", Icons.Rounded.Group) {
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

        albumShelf("From the shelves", Icons.Rounded.Album, sections.shelfAlbums, coverUrl, onOpenAlbum) {
            onOpenTab(LibraryTab.Albums, null)
        }
    }
}

private fun LazyListScope.albumShelf(
    title: String,
    icon: ImageVector,
    albums: List<Album>,
    coverUrl: (Track, Int) -> String?,
    onOpenAlbum: (Album) -> Unit,
    onOpenSection: () -> Unit,
) {
    if (albums.isEmpty()) return
    item(key = "$title-header") { SectionHeader(title, icon, onOpenSection) }
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

@Composable
private fun SectionHeader(title: String, icon: ImageVector, onClick: () -> Unit) {
    val colors = MhTheme.colors
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick)
            .padding(horizontal = 16.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Icon(icon, contentDescription = null, tint = colors.mutedForeground, modifier = Modifier.size(16.dp))
        Text(
            title,
            style = MaterialTheme.typography.titleMedium,
            fontWeight = FontWeight.SemiBold,
            color = colors.foreground,
        )
        Icon(
            Icons.AutoMirrored.Rounded.KeyboardArrowRight,
            contentDescription = null,
            tint = colors.mutedForeground,
            modifier = Modifier.size(16.dp),
        )
    }
}

/** A 136dp album tile, the width the web's shelves use on a phone. */
@Composable
private fun AlbumShelfTile(album: Album, coverUrl: String?, onClick: () -> Unit) {
    val colors = MhTheme.colors
    Column(modifier = Modifier.width(136.dp).clickable(onClick = onClick)) {
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
        modifier = Modifier.width(120.dp).clickable(onClick = onClick),
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

/** One of the ten favourites, as a bordered card row. Tapping plays the whole favourites list. */
@Composable
private fun FavouriteRow(track: Track, coverUrl: String?, isPlaying: Boolean, onClick: () -> Unit) {
    val colors = MhTheme.colors
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 16.dp, vertical = 3.dp)
            .clip(RoundedCornerShape(8.dp))
            .background(colors.card)
            .border(1.dp, colors.border, RoundedCornerShape(8.dp))
            .clickable(onClick = onClick)
            .padding(8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Artwork(
            url = coverUrl,
            artist = track.albumArtist,
            title = track.album,
            modifier = Modifier.size(44.dp),
        )
        Spacer(Modifier.width(12.dp))
        Column(modifier = Modifier.weight(1f)) {
            Text(
                track.title,
                style = MaterialTheme.typography.bodyMedium,
                fontWeight = FontWeight.Medium,
                color = if (isPlaying) colors.primary else colors.foreground,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            Text(
                track.albumArtist,
                style = MaterialTheme.typography.bodySmall,
                color = colors.mutedForeground,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
        Spacer(Modifier.width(8.dp))
        Icon(
            Icons.Rounded.Favorite,
            contentDescription = null,
            tint = colors.primary,
            modifier = Modifier.size(15.dp),
        )
    }
}
