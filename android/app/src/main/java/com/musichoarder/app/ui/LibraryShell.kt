package com.musichoarder.app.ui

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.lazy.LazyListState
import androidx.compose.foundation.lazy.grid.LazyGridState
import androidx.compose.foundation.lazy.grid.rememberLazyGridState
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ListAlt
import androidx.compose.material.icons.automirrored.rounded.ListAlt
import androidx.compose.material.icons.outlined.Album
import androidx.compose.material.icons.outlined.GridView
import androidx.compose.material.icons.outlined.Group
import androidx.compose.material.icons.rounded.Album
import androidx.compose.material.icons.rounded.AutoAwesome
import androidx.compose.material.icons.rounded.FilterList
import androidx.compose.material.icons.rounded.GridView
import androidx.compose.material.icons.rounded.Group
import androidx.compose.material.icons.rounded.Person
import androidx.compose.material.icons.rounded.SwapVert
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.NavigationBarItemDefaults
import androidx.compose.material3.NavigationRail
import androidx.compose.material3.NavigationRailItem
import androidx.compose.material3.NavigationRailItemDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.VerticalDivider
import androidx.compose.runtime.Composable
import androidx.compose.runtime.Stable
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.musichoarder.app.data.ALBUM_SORT_LABELS
import com.musichoarder.app.data.AccountsState
import com.musichoarder.app.data.Album
import com.musichoarder.app.data.AlbumSortKey
import com.musichoarder.app.data.AlbumStatus
import com.musichoarder.app.data.CHIP_KEYS
import com.musichoarder.app.data.CHIP_LABELS
import com.musichoarder.app.data.ArtistGroup
import com.musichoarder.app.data.ArtistMode
import com.musichoarder.app.data.ChipKey
import com.musichoarder.app.data.LibraryContent
import com.musichoarder.app.data.LibraryState
import com.musichoarder.app.data.LibraryTab
import com.musichoarder.app.data.LibraryUiState
import com.musichoarder.app.data.NowPlayingLinks
import com.musichoarder.app.data.SORT_LABELS
import com.musichoarder.app.data.SortKey
import com.musichoarder.app.data.Track
import com.musichoarder.app.data.greetingForHour
import com.musichoarder.app.data.likedNow
import com.musichoarder.app.data.resolveNowPlayingLinks
import com.musichoarder.app.data.rowSharedByLabelFor
import com.musichoarder.app.data.visibleChipKeys
import com.musichoarder.app.data.visibleSortKeys
import com.musichoarder.app.ui.theme.MhTheme
import java.util.Calendar
import kotlinx.coroutines.launch

/** Everything the shell needs to drive the four tabs, so the parameter list stays readable. */
class LibraryActions(
    val onSelectTab: (LibraryTab) -> Unit,
    val onQueryChange: (String) -> Unit,
    val onToggleChip: (ChipKey) -> Unit,
    /** Exactly this chip, whatever was pressed before: the Overview's "See all". */
    val onShowChip: (ChipKey) -> Unit,
    val onClearChips: () -> Unit,
    /** A sort key from the menu. Picking the current one again is the menu's no-op, not a flip. */
    val onSetSort: (SortKey) -> Unit,
    /** The menu's Ascending / Descending pair, which names a direction rather than flipping it. */
    val onSetSortAscending: (Boolean) -> Unit,
    val onSetAlbumSort: (AlbumSortKey) -> Unit,
    val onToggleUnreleased: () -> Unit,
    val onSetArtistMode: (ArtistMode) -> Unit,
    val onOpenArtist: (ArtistGroup) -> Unit,
    val onClearArtistFilter: () -> Unit,
    val onOpenAlbum: (Album) -> Unit,
    /** A row's "Go to album", which knows the card's key rather than the card. */
    val onOpenAlbumKey: (String) -> Unit,
    /** A row's "Go to artist", which knows the lead artist's name rather than the group. */
    val onOpenArtistName: (String) -> Unit,
    val onToggleLike: (Track) -> Unit,
    /** The Play / Shuffle pills: always from the top. */
    val onPlay: (List<Track>, Int) -> Unit,
    /** A tap on a track row, which follows the row tap rule instead (see `RowTap`). */
    val onActivateRow: (List<Track>, Int) -> Unit,
    val onShuffle: (List<Track>) -> Unit,
    val onRefresh: () -> Unit,
    val onUnpair: () -> Unit,
    val onSwitchAccount: (Int) -> Unit,
    val onAddAccount: () -> Unit,
)

/**
 * The four tabs' scroll positions, one per tab.
 *
 * Held by the root rather than by each tab, for two reasons. A tab switch takes the old tab out of
 * composition, and the album drill-in takes the whole shell out — both used to throw the position
 * away, so coming back from an album landed you at the top of a 2,000-track list. And the
 * navigation bar's re-tap ("take me to the top") and the pinned title need to reach the list from
 * outside it. The states are `rememberSaveable` underneath, so they survive rotation too.
 */
@Stable
class LibraryListStates(
    val overview: LazyListState,
    val albums: LazyGridState,
    val artists: LazyGridState,
    val tracks: LazyListState,
) {
    /**
     * Whether [tab]'s large title has scrolled out of sight — the header is always item 0, so that
     * is "past item 0", or far enough into it that the title row has gone.
     */
    fun isPastTitle(tab: LibraryTab, titlePx: Int): Boolean {
        val (index, offset) = when (tab) {
            LibraryTab.Overview -> overview.firstVisibleItemIndex to overview.firstVisibleItemScrollOffset
            LibraryTab.Albums -> albums.firstVisibleItemIndex to albums.firstVisibleItemScrollOffset
            LibraryTab.Artists -> artists.firstVisibleItemIndex to artists.firstVisibleItemScrollOffset
            LibraryTab.Tracks -> tracks.firstVisibleItemIndex to tracks.firstVisibleItemScrollOffset
        }
        return index > 0 || offset > titlePx
    }

    /**
     * Back to the header. A long way down it jumps rather than animating: an animated scroll across
     * a thousand rows is a second of blur that tells you nothing.
     */
    suspend fun scrollToTop(tab: LibraryTab) {
        when (tab) {
            LibraryTab.Overview -> overview.toTop()
            LibraryTab.Albums -> albums.toTop()
            LibraryTab.Artists -> artists.toTop()
            LibraryTab.Tracks -> tracks.toTop()
        }
    }
}

private const val JUMP_INSTEAD_OF_ANIMATING = 30

private suspend fun LazyListState.toTop() =
    if (firstVisibleItemIndex > JUMP_INSTEAD_OF_ANIMATING) scrollToItem(0) else animateScrollToItem(0)

private suspend fun LazyGridState.toTop() =
    if (firstVisibleItemIndex > JUMP_INSTEAD_OF_ANIMATING) scrollToItem(0) else animateScrollToItem(0)

@Composable
fun rememberLibraryListStates(): LibraryListStates {
    val overview = rememberLazyListState()
    val albums = rememberLazyGridState()
    val artists = rememberLazyGridState()
    val tracks = rememberLazyListState()
    return remember(overview, albums, artists, tracks) {
        LibraryListStates(overview, albums, artists, tracks)
    }
}

/**
 * The library shell: whichever tab is showing, each opening on its own header.
 *
 * The port of the web's compact Listen pages. Every tab's header is the first item of its list —
 * a 28sp title (the greeting on Overview), a short meta line, the sort / filter button and the
 * account avatar, then the search field and whatever the tab narrows by — so it scrolls away and a
 * phone's screen goes to the music. Once the title has gone, a slim pinned bar fades in carrying
 * the title and the same two buttons, the way a Material large top app bar collapses. The four
 * tabs themselves live in the navigation bar the root docks under the mini player.
 */
@Composable
fun LibraryShell(
    state: LibraryState,
    ui: LibraryUiState,
    content: LibraryContent,
    accounts: AccountsState,
    /** `/auth/me`'s `isAdmin` — picks which empty-library copy applies; see the message below. */
    isAdmin: Boolean,
    albumStatuses: Map<String, AlbumStatus>,
    likes: Map<Int, String?>,
    playingTrackId: Int?,
    isPlayingNow: Boolean,
    coverUrl: (Track, Int) -> String?,
    artistImageUrl: (String) -> String,
    actions: LibraryActions,
    contentPadding: PaddingValues,
    modifier: Modifier = Modifier,
    listStates: LibraryListStates = rememberLibraryListStates(),
) {
    val colors = MhTheme.colors
    val scope = rememberCoroutineScope()
    // A row's "Go to album / artist", asked of the same album cards the player's line asks, so the
    // two taps can never land on different pages.
    val linksOf: (Track) -> NowPlayingLinks? =
        remember(state) { { track: Track -> resolveNowPlayingLinks(state, track.id) } }

    val greeting = rememberGreeting()
    val title = if (ui.tab == LibraryTab.Overview) greeting else tabTitle(ui.tab)
    // Summed once per list rather than on every recomposition; a library is thousands of rows.
    val trackSeconds = remember(content.tracks) { content.tracks.sumOf { it.durationSeconds.toLong() } }
    val meta = headerMeta(ui, content, trackSeconds)

    val accountButton: @Composable () -> Unit = {
        AccountMenu(
            accounts = accounts,
            onSwitchAccount = actions.onSwitchAccount,
            onAddAccount = actions.onAddAccount,
            onUnpair = actions.onUnpair,
            onRefresh = actions.onRefresh,
        )
    }
    // One set of trailing buttons, drawn by the header and again by the pinned bar once the header
    // has gone — never both at once.
    val trailing: @Composable RowScope.() -> Unit = {
        when (ui.tab) {
            LibraryTab.Overview -> Unit
            LibraryTab.Albums -> AlbumsMenu(ui, content, actions)
            LibraryTab.Artists -> ArtistsMenu(ui, content, actions)
            LibraryTab.Tracks -> TracksMenu(ui, content, actions, isAdmin)
        }
        accountButton()
    }

    Box(modifier = modifier.fillMaxSize().background(colors.background).statusBarsPadding()) {
        val isEmptyLibrary = state.isEmpty
        val blocking: (@Composable () -> Unit)? = when {
            state.isLoading && isEmptyLibrary -> {
                { CenteredPane { CircularProgressIndicator(color = colors.primary) } }
            }

            state.error != null && isEmptyLibrary -> {
                { ErrorPane(state.error, actions.onRefresh) }
            }

            isEmptyLibrary -> {
                {
                    MessagePane(
                        if (isAdmin) {
                            "No built tracks yet.\nThe pipeline lists tracks here once it has copied " +
                                "them into the destination library."
                        } else {
                            "Nothing has been shared with you yet."
                        },
                    )
                }
            }

            else -> null
        }

        if (blocking != null) {
            // No list to scroll, but the header still stands — above all its avatar, which is how a
            // member with nothing shared yet reaches the switcher and Sign out.
            Column(modifier = Modifier.fillMaxSize()) {
                MhLargeHeader(title = title, meta = null, actions = { accountButton() })
                Box(modifier = Modifier.weight(1f)) { blocking() }
            }
        } else {
            val titlePx = with(LocalDensity.current) { PINNED_TITLE_AFTER.roundToPx() }
            val pastTitle by remember(listStates, ui.tab, titlePx) {
                derivedStateOf { listStates.isPastTitle(ui.tab, titlePx) }
            }
            val header: @Composable () -> Unit = {
                // Once the pinned bar carries the buttons, the header's own copies — half hidden
                // under it — leave the accessibility tree, so TalkBack meets each button once.
                MhLargeHeader(title = title, meta = meta, actions = trailing, actionsHidden = pastTitle) {
                    if (ui.tab != LibraryTab.Overview) {
                        MhSearchField(
                            value = ui.query,
                            onValueChange = actions.onQueryChange,
                            placeholder = searchPlaceholder(ui.tab),
                            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp),
                        )
                    }
                    FilterTokens(ui, content, actions)
                    if (ui.tab == LibraryTab.Tracks) TrackChipTokens(ui, actions)
                }
            }
            TabBody(
                ui = ui,
                content = content,
                albumStatuses = albumStatuses,
                likes = likes,
                playingTrackId = playingTrackId,
                isPlayingNow = isPlayingNow,
                coverUrl = coverUrl,
                artistImageUrl = artistImageUrl,
                actions = actions,
                linksOf = linksOf,
                listStates = listStates,
                header = header,
                contentPadding = contentPadding,
                isAdmin = isAdmin,
            )

            PinnedTitleBar(
                title = title,
                visible = pastTitle,
                onScrollToTop = { scope.launch { listStates.scrollToTop(ui.tab) } },
                actions = trailing,
            )
        }
    }
}

/** How far into the header the list has to scroll before the pinned title takes over. */
private val PINNED_TITLE_AFTER = 56.dp

@Composable
private fun TabBody(
    ui: LibraryUiState,
    content: LibraryContent,
    albumStatuses: Map<String, AlbumStatus>,
    likes: Map<Int, String?>,
    playingTrackId: Int?,
    isPlayingNow: Boolean,
    coverUrl: (Track, Int) -> String?,
    artistImageUrl: (String) -> String,
    actions: LibraryActions,
    linksOf: (Track) -> NowPlayingLinks?,
    listStates: LibraryListStates,
    header: @Composable () -> Unit,
    contentPadding: PaddingValues,
    isAdmin: Boolean,
) {
    when (ui.tab) {
        LibraryTab.Overview -> OverviewTab(
            sections = content.overview,
            coverUrl = coverUrl,
            artistImageUrl = artistImageUrl,
            playingTrackId = playingTrackId,
            isPlayingNow = isPlayingNow,
            onActivateRow = actions.onActivateRow,
            onToggleLike = actions.onToggleLike,
            linksOf = linksOf,
            onOpenAlbumKey = actions.onOpenAlbumKey,
            onOpenArtistName = actions.onOpenArtistName,
            sharedByOf = content::rowSharedByLabelFor,
            onOpenAlbum = actions.onOpenAlbum,
            onOpenArtist = actions.onOpenArtist,
            onOpenTab = { tab, chip ->
                actions.onSelectTab(tab)
                // "See all" shows that chip's list whatever was pressed before; a toggle would
                // switch an already-pressed chip off and open the whole library instead.
                chip?.let(actions.onShowChip)
            },
            contentPadding = contentPadding,
            header = header,
            listState = listStates.overview,
        )

        LibraryTab.Albums -> AlbumsTab(
            albums = content.albums,
            statuses = albumStatuses,
            coverUrl = coverUrl,
            onOpenAlbum = actions.onOpenAlbum,
            contentPadding = contentPadding,
            header = header,
            gridState = listStates.albums,
            // An empty grid because the server cannot group albums reads exactly like an empty
            // library, and the fix is on the server rather than anything the listener can do
            // here — so say which it is.
            emptyMessage = when {
                content.albums.isNotEmpty() -> null
                content.albumsUnsupported -> "Albums need a newer server. Everything else still works."
                else -> noMatchMessage(ui, "albums")
            },
        )

        LibraryTab.Artists -> ArtistsTab(
            artists = content.artists,
            artistImageUrl = artistImageUrl,
            coverUrl = coverUrl,
            onOpenArtist = actions.onOpenArtist,
            contentPadding = contentPadding,
            header = header,
            gridState = listStates.artists,
            presentLetters = content.presentLetters,
            emptyMessage = if (ui.query.isBlank()) "No artists yet." else noMatchMessage(ui, "artists"),
        )

        LibraryTab.Tracks -> TracksTab(
            tracks = content.tracks,
            likedIds = { track -> likedNow(likes, track) },
            playingTrackId = playingTrackId,
            isPlayingNow = isPlayingNow,
            coverUrl = coverUrl,
            onToggleLike = actions.onToggleLike,
            onActivateRow = actions.onActivateRow,
            contentPadding = contentPadding,
            sharedByOf = content::rowSharedByLabelFor,
            linksOf = linksOf,
            onOpenAlbumKey = actions.onOpenAlbumKey,
            onOpenArtist = actions.onOpenArtistName,
            header = header,
            listState = listStates.tracks,
            // Play and Shuffle act on the filtered list, so they follow the chips: with none
            // pressed this is the whole library, with Spotify liked pressed it is that collection.
            onPlay = { actions.onPlay(content.tracks, 0) },
            onShuffle = { actions.onShuffle(content.tracks) },
            emptyMessage = if (ui.chips.isEmpty()) noMatchMessage(ui, "tracks") else CHIPS_EMPTY_MESSAGE,
        )
    }
}

/**
 * The web's copy, because it explains the one thing that is not obvious: every chip has to match,
 * and each count tells you which one is the dead end.
 */
private const val CHIPS_EMPTY_MESSAGE =
    "No tracks match these filters.\n\nEvery active chip has to match. Each chip's number is what " +
        "you would be left with if you pressed it, so a zero shows you which one is the dead end."

/**
 * The filters a menu or a drill-in turned on, as tokens you can see and take off — the web's
 * compact token row. Only where they narrow what is on screen: the artist drill-in narrows Albums
 * and Tracks, "Unreleased only" narrows the two grids (Tracks has its own Unreleased chip).
 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun FilterTokens(ui: LibraryUiState, content: LibraryContent, actions: LibraryActions) {
    val artist = ui.artistFilter?.takeIf { ui.tab == LibraryTab.Albums || ui.tab == LibraryTab.Tracks }
    val unreleased = ui.unreleasedOnly && content.unreleasedCount > 0 &&
        (ui.tab == LibraryTab.Albums || ui.tab == LibraryTab.Artists)
    if (artist == null && !unreleased) return
    FlowRow(
        modifier = Modifier.fillMaxWidth().padding(start = 16.dp, end = 16.dp, top = 12.dp),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        if (artist != null) {
            MhFilterToken(
                label = artist,
                clearLabel = "Clear artist filter",
                onClear = actions.onClearArtistFilter,
                icon = Icons.Rounded.Person,
            )
        }
        if (unreleased) {
            MhFilterToken(
                label = "Unreleased only",
                clearLabel = "Show everything",
                onClear = actions.onToggleUnreleased,
                icon = Icons.Rounded.AutoAwesome,
            )
        }
    }
}

/**
 * The pressed filter chips as removable tokens, plus Clear — the web's compact token row. Nothing
 * at rest: the header only grows while something narrows the list.
 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun TrackChipTokens(ui: LibraryUiState, actions: LibraryActions) {
    if (ui.chips.isEmpty()) return
    FlowRow(
        modifier = Modifier.fillMaxWidth().padding(start = 16.dp, end = 16.dp, top = 4.dp),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        // In the chips' own order, so a token never jumps when another is added.
        for (key in CHIP_KEYS.filter { it in ui.chips }) {
            val label = CHIP_LABELS.getValue(key)
            MhFilterToken(
                label = label,
                clearLabel = "Remove filter $label",
                onClear = { actions.onToggleChip(key) },
            )
        }
        MhTextAction("Clear", onClick = actions.onClearChips)
    }
}

/**
 * Tracks: filter · sort by · direction · clear — the web's compact "Sort and filter" menu. The
 * filter chips live here as checkable items with their counts (the count is what pressing it would
 * leave), so the header at rest is title, meta, search and Play / Shuffle; a pressed filter shows
 * as a removable token under the search field ([TrackChipTokens]). Toggling leaves the menu open so
 * several can be combined; a filter that could only empty the list is disabled unless it is on. A
 * member gets the web's member chip set and is not offered the Spotify save date, which its shared
 * rows do not carry (`visibleChipKeys` / `visibleSortKeys`).
 */
@Composable
private fun TracksMenu(
    ui: LibraryUiState,
    content: LibraryContent,
    actions: LibraryActions,
    isAdmin: Boolean,
) {
    MhMenuButton(Icons.Rounded.SwapVert, "Sort and filter") { close ->
        MhMenuHeading("Filter")
        for (key in visibleChipKeys(isAdmin)) {
            val on = key in ui.chips
            val count = content.chipCounts[key] ?: 0
            MhMenuCheckItem(
                label = CHIP_LABELS.getValue(key),
                checked = on,
                toggle = true,
                count = count,
                enabled = on || count > 0,
                onClick = { actions.onToggleChip(key) },
            )
        }
        HorizontalDivider(color = MhTheme.colors.separator)
        MhMenuHeading("Sort by")
        for (key in visibleSortKeys(isAdmin)) {
            MhMenuCheckItem(
                label = SORT_LABELS.getValue(key),
                checked = ui.sortKey == key,
                onClick = {
                    // `setSort` flips the direction on a repeat pick, as a column header does; in a
                    // menu with its own direction items a repeat pick means nothing.
                    if (ui.sortKey != key) actions.onSetSort(key)
                    close()
                },
            )
        }
        HorizontalDivider(color = MhTheme.colors.separator)
        MhMenuCheckItem("Ascending", ui.sortAscending, onClick = { actions.onSetSortAscending(true); close() })
        MhMenuCheckItem("Descending", !ui.sortAscending, onClick = { actions.onSetSortAscending(false); close() })
        if (ui.chips.isNotEmpty()) {
            HorizontalDivider(color = MhTheme.colors.separator)
            MhMenuActionItem("Clear filters") {
                actions.onClearChips()
                close()
            }
        }
    }
}

/** Albums: sort by · unreleased only. */
@Composable
private fun AlbumsMenu(ui: LibraryUiState, content: LibraryContent, actions: LibraryActions) {
    MhMenuButton(Icons.Rounded.SwapVert, "Sort and filter") { close ->
        MhMenuHeading("Sort by")
        for (key in AlbumSortKey.entries) {
            MhMenuCheckItem(
                label = ALBUM_SORT_LABELS.getValue(key),
                checked = ui.albumSort == key,
                onClick = {
                    actions.onSetAlbumSort(key)
                    close()
                },
            )
        }
        UnreleasedItem(ui, content, actions, divider = true)
    }
}

/** Artists: which artists are listed. There is no artist sort, so the glyph is a filter. */
@Composable
private fun ArtistsMenu(ui: LibraryUiState, content: LibraryContent, actions: LibraryActions) {
    MhMenuButton(Icons.Rounded.FilterList, "View options") { _ ->
        MhMenuCheckItem(
            label = "Show featured artists",
            checked = ui.artistMode == ArtistMode.All,
            toggle = true,
            supporting = "Everyone credited on a track, not only lead artists",
            onClick = {
                actions.onSetArtistMode(
                    if (ui.artistMode == ArtistMode.All) ArtistMode.Primary else ArtistMode.All
                )
            },
        )
        UnreleasedItem(ui, content, actions, divider = false)
    }
}

/**
 * Leaks, snippets and stems, per the API's release classification. The grids have no chip row to
 * fold this into, so it is a menu toggle there (and a token while it is on); on Tracks the same
 * filter is the `unreleased` chip, which composes with the rest. Only offered when there is
 * something to show — a toggle that empties the page is not a filter.
 *
 * Toggles leave the menu open, as the web's checkbox items do, so several can be combined.
 */
@Composable
private fun UnreleasedItem(
    ui: LibraryUiState,
    content: LibraryContent,
    actions: LibraryActions,
    divider: Boolean,
) {
    if (content.unreleasedCount <= 0) return
    if (divider) HorizontalDivider(color = MhTheme.colors.separator)
    MhMenuCheckItem(
        label = "Unreleased only",
        checked = ui.unreleasedOnly,
        toggle = true,
        supporting = "Leaks, snippets and stems",
        count = content.unreleasedCount,
        onClick = actions.onToggleUnreleased,
    )
}

/**
 * The slim bar that takes over once the large title has scrolled away: the title again, and the
 * header's own trailing buttons, so sorting and the account stay one tap away however far down the
 * list you are. Tapping the title goes back to the top — Android has no status-bar tap for it.
 */
@Composable
private fun PinnedTitleBar(
    title: String,
    visible: Boolean,
    onScrollToTop: () -> Unit,
    actions: @Composable RowScope.() -> Unit,
) {
    val colors = MhTheme.colors
    AnimatedVisibility(
        visible = visible,
        enter = fadeIn(tween(150)),
        exit = fadeOut(tween(150)),
    ) {
        Column(modifier = Modifier.fillMaxWidth().background(colors.background)) {
            Row(
                modifier = Modifier.fillMaxWidth().heightIn(min = 56.dp).padding(end = 4.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    title,
                    style = MaterialTheme.typography.titleLarge,
                    color = colors.foreground,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier
                        .weight(1f)
                        .clickable(onClickLabel = "Scroll to top", onClick = onScrollToTop)
                        .padding(horizontal = 16.dp, vertical = 16.dp),
                )
                actions()
            }
            HorizontalDivider(thickness = hairline(), color = colors.separator)
        }
    }
}

/**
 * The four tabs as a Material 3 navigation bar docked at the bottom — the Android form of the
 * web's floating tab bar (a member's tabs there are these same four). Material's shape and motion
 * (the indicator pill, the filled glyph when active, the label always shown), the web's colours:
 * the tint marks the active tab on a gray lozenge, the rest read in the foreground colour.
 *
 * The bar takes the navigation-bar inset itself, so gesture and three-button navigation both sit
 * under it while the screen stays edge to edge. [onSelect] also fires for the tab that is already
 * active — the caller turns that into "back to the top".
 */
@Composable
fun LibraryNavigationBar(
    selected: LibraryTab,
    onSelect: (LibraryTab) -> Unit,
    modifier: Modifier = Modifier,
) {
    val colors = MhTheme.colors
    Column(modifier = modifier) {
        HorizontalDivider(thickness = hairline(), color = colors.separator)
        NavigationBar(
            containerColor = colors.chromeSolid,
            contentColor = colors.foreground,
            tonalElevation = 0.dp,
        ) {
            for (tab in LibraryTab.entries) {
                val isSelected = tab == selected
                NavigationBarItem(
                    selected = isSelected,
                    onClick = { onSelect(tab) },
                    icon = { Icon(tabIcon(tab, isSelected), contentDescription = null) },
                    label = { Text(tabTitle(tab), maxLines = 1, overflow = TextOverflow.Ellipsis) },
                    colors = NavigationBarItemDefaults.colors(
                        selectedIconColor = colors.primary,
                        selectedTextColor = colors.primary,
                        indicatorColor = colors.secondary,
                        unselectedIconColor = colors.foreground,
                        unselectedTextColor = colors.foreground,
                    ),
                )
            }
        }
    }
}

/**
 * The same four tabs as a navigation rail, for a window at least 600dp wide — a tablet, a foldable
 * open, a phone on its side — where a bottom bar would spend scarce height and stretch four items
 * across a wide screen.
 */
@Composable
fun LibraryNavigationRail(
    selected: LibraryTab,
    onSelect: (LibraryTab) -> Unit,
    modifier: Modifier = Modifier,
) {
    val colors = MhTheme.colors
    Row(modifier = modifier.fillMaxHeight()) {
        NavigationRail(
            containerColor = colors.chromeSolid,
            contentColor = colors.foreground,
        ) {
            Spacer(Modifier.height(8.dp))
            for (tab in LibraryTab.entries) {
                val isSelected = tab == selected
                NavigationRailItem(
                    selected = isSelected,
                    onClick = { onSelect(tab) },
                    icon = { Icon(tabIcon(tab, isSelected), contentDescription = null) },
                    label = { Text(tabTitle(tab), maxLines = 1, overflow = TextOverflow.Ellipsis) },
                    colors = NavigationRailItemDefaults.colors(
                        selectedIconColor = colors.primary,
                        selectedTextColor = colors.primary,
                        indicatorColor = colors.secondary,
                        unselectedIconColor = colors.foreground,
                        unselectedTextColor = colors.foreground,
                    ),
                )
            }
        }
        VerticalDivider(thickness = hairline(), color = colors.separator)
    }
}

/** One device pixel — the web's `--hairline`. */
@Composable
private fun hairline() = with(LocalDensity.current) { 1f.toDp() }

private fun tabTitle(tab: LibraryTab): String = when (tab) {
    LibraryTab.Overview -> "Overview"
    LibraryTab.Albums -> "Albums"
    LibraryTab.Artists -> "Artists"
    LibraryTab.Tracks -> "Tracks"
}

/** Material's convention: the filled glyph for the active destination, the outline for the rest. */
private fun tabIcon(tab: LibraryTab, selected: Boolean): ImageVector = when (tab) {
    LibraryTab.Overview -> if (selected) Icons.Rounded.GridView else Icons.Outlined.GridView
    LibraryTab.Albums -> if (selected) Icons.Rounded.Album else Icons.Outlined.Album
    LibraryTab.Artists -> if (selected) Icons.Rounded.Group else Icons.Outlined.Group
    LibraryTab.Tracks -> if (selected) Icons.AutoMirrored.Rounded.ListAlt else Icons.AutoMirrored.Outlined.ListAlt
}

private fun searchPlaceholder(tab: LibraryTab): String = when (tab) {
    LibraryTab.Albums -> "Search albums"
    LibraryTab.Artists -> "Search artists"
    else -> "Search tracks"
}

/**
 * The short line under the title: what you are looking at, and how much of it — the web's compact
 * subtitle. "N of M" once something narrows the list, the Tracks list's total playing time, and
 * who shared the rows when they are someone else's.
 */
private fun headerMeta(ui: LibraryUiState, content: LibraryContent, trackSeconds: Long): String {
    val base = when (ui.tab) {
        LibraryTab.Overview ->
            "${content.trackCount.formatGrouped()} tracks · ${content.libraryAlbumCount.formatGrouped()} albums · " +
                "${content.artistCount.formatGrouped()} artists"

        LibraryTab.Albums -> narrowed(content.albums.size, content.albumCount, "album")
        LibraryTab.Artists -> narrowed(content.artists.size, content.artistCount, "artist")
        LibraryTab.Tracks -> {
            val head = narrowed(content.tracks.size, content.trackListCount, "track")
            // Nothing to total when the list is empty — the empty state explains itself.
            if (content.tracks.isEmpty()) head else "$head · ${formatTotalDuration(trackSeconds)}"
        }
    }
    // Names whoever actually shared the rows on screen. Absent when it is all your own music, so
    // an admin's header reads exactly as it did before.
    return content.sharedByLabel?.let { "$base · $it" } ?: base
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

/**
 * [MessagePane] for inside a list: the same copy, but sized to its text, because a lazy item has no
 * height to centre in — and the header above it (the search box, the chips) has to stay reachable
 * so the listener can undo whatever emptied the list.
 */
@Composable
fun ListMessage(message: String, modifier: Modifier = Modifier) {
    Text(
        message,
        style = MaterialTheme.typography.bodyMedium,
        color = MhTheme.colors.mutedForeground,
        textAlign = TextAlign.Center,
        modifier = modifier.fillMaxWidth().padding(horizontal = 32.dp, vertical = 48.dp),
    )
}

@Composable
fun ErrorPane(message: String, onRetry: () -> Unit) {
    val colors = MhTheme.colors
    CenteredPane {
        Column(
            horizontalAlignment = Alignment.CenterHorizontally,
            modifier = Modifier
                .padding(28.dp)
                // A fill, not the card colour: the card is the page colour on a plain page.
                .background(colors.muted, RoundedCornerShape(12.dp))
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
