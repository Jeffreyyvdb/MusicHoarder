package com.musichoarder.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
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
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ListAlt
import androidx.compose.material.icons.rounded.Album
import androidx.compose.material.icons.rounded.AutoAwesome
import androidx.compose.material.icons.rounded.Close
import androidx.compose.material.icons.rounded.GridView
import androidx.compose.material.icons.rounded.Group
import androidx.compose.material.icons.rounded.PlayArrow
import androidx.compose.material.icons.rounded.Refresh
import androidx.compose.material.icons.rounded.Shuffle
import androidx.compose.material.icons.rounded.SwapVert
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.musichoarder.app.data.ALBUM_SORT_LABELS
import com.musichoarder.app.data.AccountsState
import com.musichoarder.app.data.Album
import com.musichoarder.app.data.AlbumSortKey
import com.musichoarder.app.data.AlbumStatus
import com.musichoarder.app.data.ArtistGroup
import com.musichoarder.app.data.ArtistMode
import com.musichoarder.app.data.ChipKey
import com.musichoarder.app.data.LibraryContent
import com.musichoarder.app.data.LibraryState
import com.musichoarder.app.data.LibraryTab
import com.musichoarder.app.data.LibraryUiState
import com.musichoarder.app.data.SORT_LABELS
import com.musichoarder.app.data.SortKey
import com.musichoarder.app.data.Track
import com.musichoarder.app.data.greetingForHour
import com.musichoarder.app.ui.theme.MhTheme
import java.util.Calendar

/** Everything the shell needs to drive the four tabs, so the parameter list stays readable. */
class LibraryActions(
    val onSelectTab: (LibraryTab) -> Unit,
    val onQueryChange: (String) -> Unit,
    val onToggleChip: (ChipKey) -> Unit,
    val onClearChips: () -> Unit,
    val onSetSort: (SortKey) -> Unit,
    val onSetAlbumSort: (AlbumSortKey) -> Unit,
    val onToggleUnreleased: () -> Unit,
    val onSetArtistMode: (ArtistMode) -> Unit,
    val onSetLetter: (String?) -> Unit,
    val onOpenArtist: (ArtistGroup) -> Unit,
    val onClearArtistFilter: () -> Unit,
    val onOpenAlbum: (Album) -> Unit,
    val onToggleLike: (Track) -> Unit,
    val onPlay: (List<Track>, Int) -> Unit,
    val onShuffle: (List<Track>) -> Unit,
    val onRefresh: () -> Unit,
    val onUnpair: () -> Unit,
    val onSwitchAccount: (Int) -> Unit,
    val onAddAccountScanned: (String) -> Unit,
    val onScanError: (String) -> Unit,
)

/**
 * The library shell: the four section pills, a per-tab toolbar, and whichever page is showing.
 *
 * Laid out like the web's Listen section - the top bar's sections are pills, the page toolbar
 * carries the title and the search field - but stacked into two bands rather than one, because a
 * 44px bar that fits a title, a search box, a sort control and two buttons on a desktop fits none of
 * them at 411dp.
 */
@Composable
fun LibraryShell(
    state: LibraryState,
    ui: LibraryUiState,
    content: LibraryContent,
    accounts: AccountsState,
    albumStatuses: Map<String, AlbumStatus>,
    likes: Map<Int, String?>,
    playingTrackId: Int?,
    isPlayingNow: Boolean,
    coverUrl: (Track, Int) -> String?,
    artistImageUrl: (String) -> String,
    actions: LibraryActions,
    contentPadding: PaddingValues,
    modifier: Modifier = Modifier,
) {
    val colors = MhTheme.colors

    Column(modifier = modifier.fillMaxSize().background(colors.background)) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .statusBarsPadding()
                .padding(horizontal = 8.dp, vertical = 6.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Row(
                modifier = Modifier
                    .weight(1f)
                    .horizontalScroll(rememberScrollState()),
                horizontalArrangement = Arrangement.spacedBy(2.dp),
            ) {
                for (tab in LibraryTab.entries) {
                    MhTabPill(tabTitle(tab), ui.tab == tab) { actions.onSelectTab(tab) }
                }
            }
            Spacer(Modifier.size(6.dp))
            MhIconButton(Icons.Rounded.Refresh, "Refresh library", actions.onRefresh)
            Spacer(Modifier.size(6.dp))
            AccountMenu(
                accounts = accounts,
                onSwitchAccount = actions.onSwitchAccount,
                onAddAccountScanned = actions.onAddAccountScanned,
                onScanError = actions.onScanError,
                onUnpair = actions.onUnpair,
            )
        }
        HorizontalDivider(color = colors.border)

        Toolbar(ui = ui, content = content, actions = actions)
        HorizontalDivider(color = colors.border)

        ui.artistFilter?.let { name ->
            ArtistFilterPill(name = name, onClear = actions.onClearArtistFilter)
        }

        val isEmptyLibrary = state.isEmpty
        when {
            state.isLoading && isEmptyLibrary ->
                CenteredPane { CircularProgressIndicator(color = colors.primary) }

            state.error != null && isEmptyLibrary -> ErrorPane(state.error, actions.onRefresh)

            isEmptyLibrary -> MessagePane(
                "No built tracks yet.\nThe pipeline lists songs here once it has copied them into " +
                    "the destination library."
            )

            ui.tab == LibraryTab.Overview -> OverviewTab(
                sections = content.overview,
                coverUrl = coverUrl,
                artistImageUrl = artistImageUrl,
                playingTrackId = playingTrackId,
                onPlayTracks = actions.onPlay,
                onOpenAlbum = actions.onOpenAlbum,
                onOpenArtist = actions.onOpenArtist,
                onOpenTab = { tab, chip ->
                    actions.onSelectTab(tab)
                    chip?.let(actions.onToggleChip)
                },
                contentPadding = contentPadding,
            )

            ui.tab == LibraryTab.Albums -> if (content.albums.isEmpty()) {
                MessagePane(noMatchMessage(ui, "albums"))
            } else {
                AlbumsTab(
                    albums = content.albums,
                    statuses = albumStatuses,
                    coverUrl = coverUrl,
                    onOpenAlbum = actions.onOpenAlbum,
                    contentPadding = contentPadding,
                )
            }

            ui.tab == LibraryTab.Artists -> ArtistsTab(
                artists = content.artists,
                presentLetters = content.presentLetters,
                letter = ui.letter,
                mode = ui.artistMode,
                artistImageUrl = artistImageUrl,
                coverUrl = coverUrl,
                onSelectLetter = actions.onSetLetter,
                onSelectMode = actions.onSetArtistMode,
                onOpenArtist = actions.onOpenArtist,
                contentPadding = contentPadding,
            )

            else -> TracksTab(
                tracks = content.tracks,
                chips = ui.chips,
                chipCounts = content.chipCounts,
                likedIds = { track -> com.musichoarder.app.data.likedNow(likes, track) },
                playingTrackId = playingTrackId,
                isPlayingNow = isPlayingNow,
                coverUrl = coverUrl,
                onToggleChip = actions.onToggleChip,
                onToggleLike = actions.onToggleLike,
                onPlay = actions.onPlay,
                contentPadding = contentPadding,
            )
        }
    }
}

/**
 * Title, live counts and the page's own controls.
 *
 * Overview gets the greeting and the library's totals; the three list tabs get a search box plus
 * whatever narrows or reorders them.
 */
@Composable
private fun Toolbar(ui: LibraryUiState, content: LibraryContent, actions: LibraryActions) {
    val colors = MhTheme.colors
    val isTracks = ui.tab == LibraryTab.Tracks

    Column(modifier = Modifier.padding(horizontal = 12.dp, vertical = 8.dp)) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Icon(
                tabIcon(ui.tab),
                contentDescription = null,
                tint = colors.mutedForeground,
                modifier = Modifier.size(17.dp),
            )
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    if (ui.tab == LibraryTab.Overview) rememberGreeting() else tabTitle(ui.tab),
                    style = MaterialTheme.typography.titleMedium,
                    color = colors.foreground,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                Text(
                    toolbarMeta(ui, content),
                    style = MaterialTheme.typography.labelSmall,
                    color = colors.mutedForeground,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }

            if (isTracks && content.tracks.isNotEmpty()) {
                if (ui.chips.isNotEmpty()) {
                    TextButton(onClick = actions.onClearChips) {
                        Text("Clear", style = MaterialTheme.typography.labelLarge, color = colors.mutedForeground)
                    }
                }
                // Play and Shuffle act on the filtered list, so they follow the chips: with none
                // pressed this is the whole library, with Spotify Liked pressed it is that
                // collection.
                RoundIconButton(Icons.Rounded.PlayArrow, "Play", filled = true) {
                    actions.onPlay(content.tracks, 0)
                }
                RoundIconButton(Icons.Rounded.Shuffle, "Shuffle", filled = false) {
                    actions.onShuffle(content.tracks)
                }
            }
        }

        if (ui.tab != LibraryTab.Overview) {
            Spacer(Modifier.height(8.dp))
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                MhSearchField(
                    value = ui.query,
                    onValueChange = actions.onQueryChange,
                    placeholder = "Search artists, albums, tracks",
                    modifier = Modifier.weight(1f),
                )
                when (ui.tab) {
                    LibraryTab.Albums -> MhSortPill(
                        current = ui.albumSort,
                        options = AlbumSortKey.entries.map { it to ALBUM_SORT_LABELS.getValue(it) },
                        onSelect = actions.onSetAlbumSort,
                        icon = Icons.Rounded.SwapVert,
                    )

                    LibraryTab.Tracks -> MhSortPill(
                        current = ui.sortKey,
                        options = SortKey.entries.map { it to SORT_LABELS.getValue(it) },
                        onSelect = actions.onSetSort,
                        icon = Icons.Rounded.SwapVert,
                        trailing = if (ui.sortAscending) " ↑" else " ↓",
                    )

                    else -> Unit
                }
            }

            // Leaks, snippets and stems, per the API's release classification. The grids have no chip
            // row to fold this into, so it stays a standalone toggle; on Tracks the same filter is
            // the `unreleased` chip, which composes with the rest.
            if (ui.tab != LibraryTab.Tracks && content.unreleasedCount > 0) {
                Spacer(Modifier.height(8.dp))
                MhFilterChip(
                    label = "Unreleased",
                    pressed = ui.unreleasedOnly,
                    onClick = actions.onToggleUnreleased,
                    icon = Icons.Rounded.AutoAwesome,
                    count = content.unreleasedCount,
                )
            }
        }
    }
}

/** "filtering by <artist>", with a way out. */
@Composable
private fun ArtistFilterPill(name: String, onClear: () -> Unit) {
    val colors = MhTheme.colors
    Row(
        modifier = Modifier
            .padding(horizontal = 12.dp, vertical = 8.dp)
            .clip(CircleShape)
            .background(colors.primary.copy(alpha = 0.1f))
            .clickable(onClick = onClear)
            .padding(horizontal = 12.dp, vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        Text("filtering by", style = MaterialTheme.typography.bodySmall, color = colors.primary)
        Text(
            name,
            style = MaterialTheme.typography.bodySmall,
            fontWeight = FontWeight.SemiBold,
            color = colors.primary,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
        Icon(
            Icons.Rounded.Close,
            contentDescription = "Clear artist filter",
            tint = colors.primary,
            modifier = Modifier.size(13.dp),
        )
    }
}

/** A 32dp round action, the phone's stand-in for the web's labelled Play / Shuffle buttons. */
@Composable
private fun RoundIconButton(
    icon: ImageVector,
    contentDescription: String,
    filled: Boolean,
    onClick: () -> Unit,
) {
    val colors = MhTheme.colors
    Box(
        modifier = Modifier
            .size(34.dp)
            .clip(CircleShape)
            .background(if (filled) colors.primary else Color.Transparent)
            .then(
                if (filled) Modifier
                else Modifier.clip(CircleShape).background(colors.card)
            )
            .clickable(onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        Icon(
            icon,
            contentDescription = contentDescription,
            tint = if (filled) colors.primaryForeground else colors.foreground,
            modifier = Modifier.size(17.dp),
        )
    }
}

private fun tabTitle(tab: LibraryTab): String = when (tab) {
    LibraryTab.Overview -> "Overview"
    LibraryTab.Albums -> "Albums"
    LibraryTab.Artists -> "Artists"
    LibraryTab.Tracks -> "Tracks"
}

private fun tabIcon(tab: LibraryTab): ImageVector = when (tab) {
    LibraryTab.Overview -> Icons.Rounded.GridView
    LibraryTab.Albums -> Icons.Rounded.Album
    LibraryTab.Artists -> Icons.Rounded.Group
    LibraryTab.Tracks -> Icons.AutoMirrored.Rounded.ListAlt
}

/** The live summary under the title: what you are looking at, and how much of it. */
private fun toolbarMeta(ui: LibraryUiState, content: LibraryContent): String = when (ui.tab) {
    LibraryTab.Overview ->
        "${content.trackCount.formatGrouped()} tracks · ${content.libraryAlbumCount.formatGrouped()} albums · " +
            "${content.artistCount.formatGrouped()} artists"

    LibraryTab.Albums -> narrowed(content.albums.size, content.albumCount, "album")
    LibraryTab.Artists -> narrowed(content.artists.size, content.artistCount, "artist")
    LibraryTab.Tracks -> narrowed(content.tracks.size, content.trackListCount, "track")
}

/**
 * "N of M" only once something actually narrows the list. An unfiltered page reading
 * "2,115 of 2,115" is noise, and the web says so too.
 */
private fun narrowed(shown: Int, total: Int, noun: String): String =
    if (shown == total) "${shown.formatGrouped()} $noun${if (shown == 1) "" else "s"}"
    else "${shown.formatGrouped()} of ${total.formatGrouped()}"

private fun noMatchMessage(ui: LibraryUiState, noun: String): String =
    if (ui.query.isBlank()) "No $noun here." else "No $noun match \"${ui.query}\"."

/** The greeting is read once per composition; it does not need to tick over midnight. */
@Composable
private fun rememberGreeting(): String =
    remember { greetingForHour(Calendar.getInstance().get(Calendar.HOUR_OF_DAY)) }

@Composable
fun CenteredPane(content: @Composable () -> Unit) {
    Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { content() }
}

@Composable
fun MessagePane(message: String) {
    CenteredPane {
        Text(
            message,
            style = MaterialTheme.typography.bodyMedium,
            color = MhTheme.colors.mutedForeground,
            textAlign = TextAlign.Center,
            modifier = Modifier.padding(horizontal = 32.dp),
        )
    }
}

@Composable
fun ErrorPane(message: String, onRetry: () -> Unit) {
    val colors = MhTheme.colors
    CenteredPane {
        Column(
            horizontalAlignment = Alignment.CenterHorizontally,
            modifier = Modifier
                .padding(28.dp)
                .background(colors.card, RoundedCornerShape(12.dp))
                .padding(20.dp),
        ) {
            Text(
                message,
                style = MaterialTheme.typography.bodyMedium,
                color = colors.foreground,
                textAlign = TextAlign.Center,
            )
            Spacer(Modifier.height(8.dp))
            TextButton(onClick = onRetry) {
                Text("Try again", color = colors.primary)
            }
        }
    }
}
