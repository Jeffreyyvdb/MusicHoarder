package com.musichoarder.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.PlaylistAdd
import androidx.compose.material.icons.rounded.Album
import androidx.compose.material.icons.rounded.Favorite
import androidx.compose.material.icons.rounded.FavoriteBorder
import androidx.compose.material.icons.rounded.Group
import androidx.compose.material.icons.rounded.HeartBroken
import androidx.compose.material.icons.rounded.MoreVert
import androidx.compose.material.icons.rounded.Person
import androidx.compose.material.icons.rounded.PlaylistRemove
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.CustomAccessibilityAction
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.customActions
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.musichoarder.app.data.Track
import com.musichoarder.app.ui.theme.MhMenuShape
import com.musichoarder.app.ui.theme.MhTheme

/**
 * One track in a list — the port of the web's compact `TrackList` row.
 *
 * ```
 * [48 art]  Title                                   [⋮]
 *           [Review] ♥ [shared] Artist · Album
 * ```
 * The phone row carries what you need to recognise and act on a track, nothing more: no running
 * index, no duration, no always-on heart button. Liking moved into the overflow menu with the
 * album and artist links — the web's `TrackRowMenu` — and a resting heart marks what is already
 * liked, so the state is still readable at a glance even though the toggle is one tap deeper.
 *
 * Album-style lists ([showArtwork] false) lead with the track number instead, because the cover is
 * already the page's header, and are usually one line: `3  Title ♥  [⋮]`, as on the web's album
 * page.
 *
 * [isPlaying] means *loaded* — the row is the player's current song, playing or paused; it is what
 * tints the title and draws the equalizer. [isPlayingNow] animates the bars.
 */
@Composable
fun TrackRow(
    track: Track,
    coverUrl: String?,
    isPlaying: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    trackNumber: Int? = null,
    showArtwork: Boolean = true,
    isPlayingNow: Boolean = false,
    liked: Boolean = false,
    /** Add to / Remove from favourites. Null hides it — a share row, whose ids this phone cannot like. */
    onToggleLike: (() -> Unit)? = null,
    /**
     * "Shared by X" when this track came from someone else's library and the list mixes libraries;
     * null when it is yours, or when one grantor shares everything listed (the header says it).
     */
    sharedBy: String? = null,
    /** Show the resting heart. Off on the Favourites shelf, where every row is a favourite. */
    showLikedMark: Boolean = true,
    /** "Go to album"; null hides it (an unbuilt row is on no album card, or this IS the album). */
    onOpenAlbum: (() -> Unit)? = null,
    /** "Go to artist"; null hides it. */
    onOpenArtist: (() -> Unit)? = null,
    /** "Add to playlist…"; null hides it (a share row, whose ids belong to another server). */
    onAddToPlaylist: (() -> Unit)? = null,
    /** On a playlist's page, for a track added there: "Remove from playlist", last in the menu. */
    onRemoveFromPlaylist: (() -> Unit)? = null,
) {
    val colors = MhTheme.colors
    val actions = rowMenuActions(liked, onToggleLike, onAddToPlaylist, onOpenAlbum, onOpenArtist, onRemoveFromPlaylist)
    Row(
        modifier = modifier
            .fillMaxWidth()
            // 64 for a library row (the web's compact ROW_H floor, room for art and two lines);
            // an album row is often one line, and 56 is Material's one-line list item. Both are
            // floors, so a larger font scale still grows the row instead of clipping it.
            .heightIn(min = if (showArtwork) 64.dp else 56.dp)
            // The loaded row brings the player up instead of restarting the song (the callers
            // apply the tap rule), and TalkBack should say which of the two a tap will do.
            .clickable(onClickLabel = if (isPlaying) "Show player" else "Play", onClick = onClick)
            // The overflow menu's actions, reachable from the row itself without hunting for the
            // small button next to it.
            .semantics {
                customActions = actions.map { action ->
                    CustomAccessibilityAction(action.label) { action.onClick(); true }
                }
            }
            .padding(start = 16.dp, end = if (actions.isEmpty()) 16.dp else 4.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (showArtwork) {
            Box(modifier = Modifier.size(ARTWORK_SIZE)) {
                Artwork(
                    url = coverUrl,
                    artist = track.albumArtist,
                    title = track.album,
                    modifier = Modifier.matchParentSize(),
                    shape = ArtworkShape,
                )
                if (isPlaying) {
                    Box(
                        modifier = Modifier
                            .matchParentSize()
                            .background(Color.Black.copy(alpha = 0.45f), ArtworkShape),
                        contentAlignment = Alignment.Center,
                    ) {
                        EqualizerBars(playing = isPlayingNow, color = Color.White)
                    }
                }
            }
        } else {
            Box(modifier = Modifier.width(NUMBER_WIDTH), contentAlignment = Alignment.Center) {
                if (isPlaying) {
                    EqualizerBars(playing = isPlayingNow)
                } else {
                    Text(
                        text = trackNumber?.toString() ?: "–",
                        style = MaterialTheme.typography.bodyMedium.copy(fontFeatureSettings = "tnum"),
                        color = colors.mutedForeground,
                        maxLines = 1,
                    )
                }
            }
        }
        Spacer(Modifier.size(TEXT_GAP))

        // An album page names the album and its artist in the header, so its rows only add a credit
        // that differs from the lead artist (a feature, a compilation) — anything else would
        // repeat the header on every line, and most album rows end up one line tall.
        val subtitle = if (showArtwork) {
            "${track.albumArtist} · ${track.album}"
        } else {
            track.artist.takeUnless { it.equals(track.albumArtist, ignoreCase = true) }
        }
        // The resting heart leads the artist in a library list (the web's `♥ Artist · Album`) and
        // leads the title on an album page, where there is often no second line to put it on.
        val heartOnTitle = !showArtwork
        val markLiked = liked && showLikedMark
        val hasSecondLine =
            subtitle != null || track.needsReview || sharedBy != null || (markLiked && !heartOnTitle)

        Column(modifier = Modifier.weight(1f).padding(vertical = 10.dp)) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(6.dp),
            ) {
                if (markLiked && heartOnTitle) LikedMark()
                Text(
                    text = track.title,
                    style = MaterialTheme.typography.bodyLarge,
                    // The tint is spent on live state: it marks the loaded song, nothing else.
                    color = if (isPlaying) colors.primary else colors.foreground,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f, fill = false),
                )
            }
            if (hasSecondLine) {
                Spacer(Modifier.height(2.dp))
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(6.dp),
                ) {
                    // A local file still waiting on review is in the Tracks base but on no album
                    // card — the only place it is flagged.
                    if (track.needsReview) {
                        MhBadge(
                            "Review",
                            color = colors.warningText,
                            fill = colors.warning.copy(alpha = if (colors.isDark) 0.15f else 0.06f),
                        )
                    }
                    if (markLiked && !heartOnTitle) LikedMark()
                    // Absent for your own tracks, so an admin's list looks exactly as it did.
                    if (sharedBy != null) SharedMark(sharedBy)
                    if (subtitle != null) {
                        Text(
                            text = subtitle,
                            style = MaterialTheme.typography.bodyMedium,
                            color = colors.mutedForeground,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }
                }
            }
        }

        if (actions.isNotEmpty()) {
            TrackRowMenu(title = track.title, actions = actions)
        }
    }
}

/**
 * The hairline between two [TrackRow]s, inset to the text column the way the web's list (and
 * UIKit's) insets it, so the art and the numbers read as one column. One device pixel, the web's
 * `--hairline`, in the translucent separator colour.
 */
@Composable
fun TrackRowSeparator(showArtwork: Boolean = true) {
    val hairline = with(LocalDensity.current) { 1f.toDp() }
    HorizontalDivider(
        thickness = hairline,
        color = MhTheme.colors.separator,
        modifier = Modifier.padding(
            start = 16.dp + (if (showArtwork) ARTWORK_SIZE else NUMBER_WIDTH) + TEXT_GAP,
        ),
    )
}

private val ARTWORK_SIZE: Dp = 48.dp
private val ArtworkShape = RoundedCornerShape(6.dp)
private val NUMBER_WIDTH: Dp = 28.dp
private val TEXT_GAP: Dp = 12.dp

private class RowMenuAction(
    val label: String,
    val icon: ImageVector,
    val onClick: () -> Unit,
    val destructive: Boolean = false,
)

/**
 * The web `TrackRowMenu`'s middle group, minus whatever this row cannot offer — and, on a playlist's
 * page, its destructive "Remove from playlist", last as the web puts it.
 */
private fun rowMenuActions(
    liked: Boolean,
    onToggleLike: (() -> Unit)?,
    onAddToPlaylist: (() -> Unit)?,
    onOpenAlbum: (() -> Unit)?,
    onOpenArtist: (() -> Unit)?,
    onRemoveFromPlaylist: (() -> Unit)?,
): List<RowMenuAction> = buildList {
    onToggleLike?.let {
        add(
            if (liked) RowMenuAction("Remove from favourites", Icons.Rounded.HeartBroken, it)
            else RowMenuAction("Add to favourites", Icons.Rounded.FavoriteBorder, it)
        )
    }
    onAddToPlaylist?.let { add(RowMenuAction("Add to playlist…", Icons.AutoMirrored.Rounded.PlaylistAdd, it)) }
    onOpenAlbum?.let { add(RowMenuAction("Go to album", Icons.Rounded.Album, it)) }
    onOpenArtist?.let { add(RowMenuAction("Go to artist", Icons.Rounded.Person, it)) }
    onRemoveFromPlaylist?.let {
        add(RowMenuAction("Remove from playlist", Icons.Rounded.PlaylistRemove, it, destructive = true))
    }
}

/** The trailing ⋮: a full 48dp target, and a solid popover menu like the web's. */
@Composable
private fun TrackRowMenu(title: String, actions: List<RowMenuAction>) {
    val colors = MhTheme.colors
    var expanded by remember { mutableStateOf(false) }
    Box {
        IconButton(onClick = { expanded = true }) {
            Icon(
                Icons.Rounded.MoreVert,
                contentDescription = "More options for $title",
                tint = colors.mutedForeground,
            )
        }
        DropdownMenu(
            expanded = expanded,
            onDismissRequest = { expanded = false },
            shape = MhMenuShape,
            containerColor = colors.popover,
        ) {
            for (action in actions) {
                if (action.destructive) HorizontalDivider(color = colors.separator)
                DropdownMenuItem(
                    text = {
                        Text(
                            action.label,
                            style = MaterialTheme.typography.bodyMedium,
                            color = if (action.destructive) colors.destructiveText else colors.foreground,
                        )
                    },
                    leadingIcon = {
                        Icon(
                            action.icon,
                            contentDescription = null,
                            tint = if (action.destructive) colors.destructiveText else colors.mutedForeground,
                            modifier = Modifier.size(20.dp),
                        )
                    },
                    onClick = {
                        expanded = false
                        action.onClick()
                    },
                )
            }
        }
    }
}

/**
 * The resting heart: state, not a control — the toggle is the menu's "Add to / Remove from
 * favourites". Named for TalkBack as the web names it ("In favourites"), and a glyph rather than a
 * colour alone.
 */
@Composable
private fun LikedMark() {
    Icon(
        Icons.Rounded.Favorite,
        contentDescription = "In favourites",
        tint = MhTheme.colors.primary,
        modifier = Modifier.size(14.dp),
    )
}

/**
 * The web's `SharedByBadge` in its dense `icon` variant: the glyph on a gray capsule, with the
 * grantor's name for TalkBack. The page header already names who shared the rows, so spelling it
 * out on every row would only push the artist off the line.
 */
@Composable
private fun SharedMark(sharedBy: String) {
    val colors = MhTheme.colors
    Box(
        modifier = Modifier
            .semantics { contentDescription = sharedBy }
            .background(colors.secondary, CircleShape)
            .padding(horizontal = 6.dp, vertical = 2.dp),
    ) {
        Icon(
            Icons.Rounded.Group,
            contentDescription = null,
            tint = colors.mutedForeground,
            modifier = Modifier.size(12.dp),
        )
    }
}
