package com.musichoarder.app.ui

import android.Manifest
import android.os.Build
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.tween
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.WindowInsetsSides
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.only
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.SnackbarDuration
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.SnackbarResult
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.repeatOnLifecycle
import com.musichoarder.app.data.LibraryTab
import com.musichoarder.app.data.PairingUri
import com.musichoarder.app.data.RowTap
import com.musichoarder.app.data.Track
import com.musichoarder.app.data.sharedByLabelFor
import com.musichoarder.app.ui.theme.MhTheme
import kotlinx.coroutines.launch

/**
 * The whole app in one place: pair, browse, play. Navigation is the library's four tabs plus two
 * overlays deep, which is exactly as much as a player needs - no nav graph to maintain yet.
 */
@Composable
fun MusicHoarderRoot(viewModel: AppViewModel, modifier: Modifier = Modifier) {
    val session by viewModel.session.collectAsStateWithLifecycle()
    val accounts by viewModel.accounts.collectAsStateWithLifecycle()
    val library by viewModel.library.collectAsStateWithLifecycle()
    val ui by viewModel.ui.collectAsStateWithLifecycle()
    val content by viewModel.content.collectAsStateWithLifecycle()
    val likes by viewModel.likes.collectAsStateWithLifecycle()
    val albumStatuses by viewModel.albumStatuses.collectAsStateWithLifecycle()
    val openAlbum by viewModel.openAlbum.collectAsStateWithLifecycle()
    // This phone's player, or the account's session while it plays on another device (or is only
    // remembered) — the same shape either way, so every surface below renders both.
    val playerState by viewModel.nowPlaying.collectAsStateWithLifecycle()
    val devicePicker by viewModel.devicePicker.collectAsStateWithLifecycle()
    val pairError by viewModel.pairError.collectAsStateWithLifecycle()
    val lyricsState by viewModel.lyrics.collectAsStateWithLifecycle()
    val videoState by viewModel.video.state.collectAsStateWithLifecycle()
    val pendingPairingHost by viewModel.pendingPairingHost.collectAsStateWithLifecycle()
    val likedIds by viewModel.likedIds.collectAsStateWithLifecycle()
    val share by viewModel.share.collectAsStateWithLifecycle()
    val invite by viewModel.invite.collectAsStateWithLifecycle()
    val isShareQueue by viewModel.isShareQueue.collectAsStateWithLifecycle()
    val addingAccount by viewModel.addingAccount.collectAsStateWithLifecycle()
    val nowPlayingLinks by viewModel.nowPlayingLinks.collectAsStateWithLifecycle()
    val isAdmin by viewModel.isAdmin.collectAsStateWithLifecycle()

    // Saveable, not remembered: a rotation or a trip through process death used to drop the open
    // player. The open album moved into the ViewModel with the rest of the library's view state.
    var showNowPlaying by rememberSaveable { mutableStateOf(false) }
    var showVideoBackdrop by rememberSaveable { mutableStateOf(true) }
    val snackbarHost = remember { SnackbarHostState() }
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    // Held here, above the album drill-in and the tab switch that each take a tab's list out of
    // composition, so coming back lands where you left. Keyed on the account: another account's
    // library is another list, and should open at its top.
    val listStates = key(accounts.activeIndex, accounts.active?.baseUrl) { rememberLibraryListStates() }

    // The media notification is the playback controls - without it, background playback is invisible.
    val notificationPermission = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { }
    LaunchedEffect(session, share) {
        // A share plays music too — its media notification needs the permission just the same.
        if ((session != null || share != null) && Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
    }

    // A failed heart reverts itself; saying nothing would just look like the tap missed.
    LaunchedEffect(Unit) {
        viewModel.messages.collect { snackbarHost.showSnackbar(it) }
    }

    // Playback sync's news, the web's toasts word for word: "Now playing on MacBook" when another
    // device took the music, "Couldn’t reach MacBook" when a command did not land — with Play here
    // where picking it up on this phone is the obvious next step.
    LaunchedEffect(Unit) {
        viewModel.connectNotices.collect { notice ->
            val result = snackbarHost.showSnackbar(
                message = notice.message,
                actionLabel = if (notice.offerPlayHere) "Play here" else null,
                duration = if (notice.offerPlayHere) SnackbarDuration.Long else SnackbarDuration.Short,
            )
            if (result == SnackbarResult.ActionPerformed) viewModel.playHere()
        }
    }

    // Re-read identity and capabilities whenever the app comes back to the foreground. Capabilities
    // are granted server-side by an admin; without this, turning one off would have no effect on an
    // already-paired phone until it re-paired, which nobody would think to do.
    val lifecycleOwner = LocalLifecycleOwner.current
    LaunchedEffect(lifecycleOwner, session) {
        lifecycleOwner.lifecycle.repeatOnLifecycle(Lifecycle.State.RESUMED) {
            viewModel.refreshIdentity()
        }
    }

    pendingPairingHost?.let { host ->
        AlertDialog(
            onDismissRequest = viewModel::dismissPendingPairingLink,
            title = { Text("Add this account?") },
            text = {
                Text(
                    "This link points at $host. The account it signs in is added alongside the " +
                        "one this phone is using now (which stays signed in) and becomes active " +
                        "— playback stops while the library switches."
                )
            },
            confirmButton = {
                TextButton(onClick = viewModel::confirmPendingPairingLink) { Text("Add account") }
            },
            dismissButton = {
                TextButton(onClick = viewModel::dismissPendingPairingLink) { Text("Cancel") }
            },
        )
    }

    // The share viewer and the invite flow are the two surfaces that work without a pairing —
    // an App Link must never dead-end on the pairing screen. `addingAccount` takes the same screen
    // over a signed-in app, so the switcher's "Add account" offers every way in rather than the QR
    // scanner alone; the account underneath keeps playing and is still there on Cancel.
    if ((session == null && share == null && invite == null) || addingAccount) {
        val emailSentTo by viewModel.emailLinkSentTo.collectAsStateWithLifecycle()
        BackHandler(enabled = addingAccount) { viewModel.cancelAddAccount() }
        PairScreen(
            error = pairError,
            emailSentTo = emailSentTo,
            // Nobody should have to type the public host, and a second account is nearly always
            // on the server this phone already talks to. Both stay one tap from being overridden.
            defaultBaseUrl = session?.baseUrl ?: PairingUri.DEFAULT_BASE_URL,
            isAddingAccount = addingAccount,
            activeAccountLabel = accounts.active?.label,
            onScanned = viewModel::pairFromCode,
            onManual = viewModel::pairManually,
            onRequestEmailLink = viewModel::requestEmailLink,
            // The Activity, not the Application: the system draws the passkey sheet over it.
            onUsePasskey = { baseUrl -> viewModel.signInWithPasskey(context, baseUrl) },
            onError = viewModel::setPairError,
            onCancel = viewModel::cancelAddAccount,
            modifier = modifier,
        )
        return
    }

    // Lyrics and the video clip are per-song extras the library dump does not carry.
    LaunchedEffect(playerState.trackId) {
        viewModel.onNowPlayingTrackChanged(playerState.trackId)
    }

    // The album cards' provider-link dots. One batch request per distinct album set, so the silent
    // refetches do not re-post the whole library.
    LaunchedEffect(library.albums) {
        if (library.albums.isNotEmpty()) viewModel.ensureAlbumStatuses(library.albums)
    }

    // The clip chases the audio clock, and only while the player is actually on screen - decoding
    // video behind a closed sheet would burn battery for nothing. `sync` is also the only thing that
    // ever starts the video, so closing the sheet has to park it explicitly; otherwise it just keeps
    // streaming with nothing left running to stop it.
    LaunchedEffect(showNowPlaying, showVideoBackdrop, playerState.positionMs, playerState.isPlaying) {
        if (showNowPlaying && showVideoBackdrop) {
            viewModel.video.sync(playerState.positionMs, playerState.isPlaying)
        }
    }
    LaunchedEffect(showNowPlaying, showVideoBackdrop) {
        if (!showNowPlaying || !showVideoBackdrop) viewModel.video.pause()
    }

    // The library's search box stays composed behind the player, and a focused text field keeps its
    // input connection alive: the keyboard stayed up over the sheet, and the system then restored it
    // on every resume - so the app came back from the background with a keyboard over the playing
    // song. Playing a track ends the search, so the focus goes with it.
    val focusManager = LocalFocusManager.current
    LaunchedEffect(showNowPlaying) {
        if (showNowPlaying) focusManager.clearFocus()
    }

    // The row tap rule, shared with the web (`RowTap`): a row that is not loaded plays the list from
    // there and leaves you on the list, where the mini player picks it up; the loaded row brings the
    // player up instead of restarting the song. Either way the tap ends a search, so the focus goes
    // too — the player need not open for the keyboard to be in the way.
    val activateRow: (List<Track>, Int) -> Unit = { tracks, index ->
        focusManager.clearFocus()
        if (viewModel.activateRow(tracks, index) == RowTap.OpenPlayer) showNowPlaying = true
    }

    // One ordered list rather than nested ifs, so it is obvious what Back unwinds and in what order.
    val backSteps: List<Pair<Boolean, () -> Unit>> = listOf(
        showNowPlaying to { showNowPlaying = false },
        (invite != null) to viewModel::dismissInvite,
        (share != null) to viewModel::closeShare,
        (openAlbum != null) to viewModel::closeAlbum,
        (ui.artistFilter != null) to viewModel::clearArtistFilter,
        (ui.tab != LibraryTab.Overview) to { viewModel.selectTab(LibraryTab.Overview) },
    )
    val backStep = backSteps.firstOrNull { it.first }?.second
    BackHandler(enabled = backStep != null) { backStep?.invoke() }

    // The player's "Shared by X", from the library row the queue item came from. A share queue's ids
    // belong to another server, so one that collides with a library id must not borrow its grantor.
    val nowPlayingSharedBy = remember(playerState.trackId, library.trackListBase, content, isShareQueue) {
        if (isShareQueue) null
        else library.trackListBase.firstOrNull { it.id == playerState.trackId }?.let(content::sharedByLabelFor)
    }

    // Which row reads as loaded (tinted, the equalizer, "Show player" for TalkBack), per surface.
    // A share queue's ids belong to another server and can collide with this library's, so the
    // library's rows only answer for a library queue and the share viewer's only for a share one —
    // the same rule `activateRow` applies to the tap, so what a row shows and what it does agree.
    val libraryPlayingId = if (isShareQueue) null else playerState.trackId
    val sharePlayingId = if (isShareQueue) playerState.trackId else null

    // Leaving the player for a library page: the sheet comes down, and so does anything stacked over
    // the library that would otherwise be what it lands on. Only the share viewer can be, and it is
    // never the queue that is playing when these links exist — they resolve for paired rows only —
    // so closing it here can never stop the music.
    val leaveForLibrary = {
        showNowPlaying = false
        if (share != null) viewModel.closeShare()
    }

    // Play and Shuffle start the music and leave you where you are, the web's rule: the mini player
    // picks it up, and a tap on it (or on the loaded row) brings the full player up. Either way a
    // search is over, so the keyboard goes.
    val playFromTop: (List<Track>, Int) -> Unit = { tracks, index ->
        focusManager.clearFocus()
        viewModel.play(tracks, index)
    }
    val shuffle: (List<Track>) -> Unit = { tracks -> playFromTop(tracks.shuffled(), 0) }

    // Every way of choosing a tab goes through here. The artist drill-in narrows Albums and Tracks;
    // Overview and Artists are not narrowed by it, so heading there leaves it behind rather than
    // keeping a filter alive on a page that cannot show it (where Back would then clear something
    // invisible before doing what it says).
    val selectTab: (LibraryTab) -> Unit = { tab ->
        if (tab == LibraryTab.Overview || tab == LibraryTab.Artists) viewModel.clearArtistFilter()
        viewModel.selectTab(tab)
    }
    // The navigation bar: a tab that is not showing is a switch; the showing one returns to its
    // root — out of an album, off the Albums drill-in — and at the root goes back to the top, the
    // Material and the web tab bar's re-tap alike.
    val onTabBar: (LibraryTab) -> Unit = { tab ->
        when {
            openAlbum != null -> {
                viewModel.closeAlbum()
                if (tab != ui.tab) selectTab(tab)
            }

            tab != ui.tab -> selectTab(tab)
            tab == LibraryTab.Albums && ui.artistFilter != null -> viewModel.clearArtistFilter()
            else -> scope.launch { listStates.scrollToTop(tab) }
        }
    }

    val nowPlayingVisible = showNowPlaying && playerState.isActive

    // The device picker, for the mini player's device line and Now Playing's Devices button. Only
    // while the feature is on (`devicesAvailable`: signed in, not the demo), the web's rule.
    val devices = if (playerState.devicesAvailable) {
        DevicesControl(
            picker = devicePicker,
            thisDeviceName = viewModel.thisDeviceName,
            thisDeviceKind = viewModel.thisDeviceKind,
            onChoose = viewModel::chooseDevice,
        )
    } else {
        null
    }

    BoxWithConstraints(modifier = modifier.fillMaxSize().background(MhTheme.colors.background)) {
        // The share viewer and the invite are takeovers of their own, with Close rather than a
        // place in the library, so they get no tabs.
        val showTabs = invite == null && share == null
        // A rail from 600dp, Material's medium window: a tablet, an open foldable, a phone on its
        // side — where a bottom bar spends scarce height and spreads four items across the width.
        val useRail = maxWidth >= 600.dp

        Row(modifier = Modifier.fillMaxSize()) {
            if (showTabs && useRail) {
                LibraryNavigationRail(selected = ui.tab, onSelect = onTabBar)
            }
            Column(
                modifier = Modifier
                    .weight(1f)
                    .fillMaxHeight()
                    // A side navigation bar or cutout in landscape; the rail takes the start one.
                    .windowInsetsPadding(
                        WindowInsets.safeDrawing.only(
                            if (showTabs && useRail) WindowInsetsSides.End else WindowInsetsSides.Horizontal,
                        ),
                    ),
            ) {
                Box(modifier = Modifier.weight(1f)) {
                    val album = openAlbum
                    val inviteState = invite
                    val shareState = share
                    if (inviteState != null) {
                        InviteScreen(
                            state = inviteState,
                            currentHost = session?.baseUrl?.substringAfter("://"),
                            onAccept = viewModel::acceptInvite,
                            onDismiss = viewModel::dismissInvite,
                            onRetry = viewModel::retryInvite,
                        )
                    } else if (shareState != null) {
                        ShareScreen(
                            state = shareState,
                            playingTrackId = sharePlayingId,
                            isPlayingNow = playerState.isPlaying,
                            onPlay = { tracks, index -> viewModel.playShare(tracks, index) },
                            onShuffle = { tracks -> viewModel.playShare(tracks.shuffled(), 0) },
                            onClose = viewModel::closeShare,
                            onRetry = viewModel::retryShare,
                            contentPadding = PaddingValues(bottom = 12.dp),
                            onActivateRow = { tracks, index ->
                                if (viewModel.activateShareRow(tracks, index) == RowTap.OpenPlayer) {
                                    showNowPlaying = true
                                }
                            },
                        )
                    } else if (album != null) {
                        AlbumScreen(
                            album = album,
                            // An album belongs to a single grantor — grant scoping never mixes
                            // owners into one album — so the first attributed track answers for
                            // all of them.
                            sharedBy = album.tracks.firstNotNullOfOrNull(content::sharedByLabelFor),
                            coverUrl = { track, size -> viewModel.coverUrl(track.id, track.hasCover, size) },
                            playingTrackId = libraryPlayingId,
                            likes = likes,
                            onToggleLike = viewModel::toggleLike,
                            onPlay = playFromTop,
                            onShuffle = shuffle,
                            onBack = viewModel::closeAlbum,
                            contentPadding = PaddingValues(bottom = 12.dp),
                            onActivateRow = activateRow,
                            onOpenArtist = viewModel::openArtist,
                            isPlayingNow = playerState.isPlaying,
                            // A share's ids belong to another server and can collide with this
                            // album's; its song is never "one of these".
                            onPlayPause = if (isShareQueue) null else viewModel::togglePlayPause,
                        )
                    } else {
                        LibraryShell(
                            state = library,
                            ui = ui,
                            content = content,
                            accounts = accounts,
                            isAdmin = isAdmin,
                            albumStatuses = albumStatuses,
                            likes = likes,
                            playingTrackId = libraryPlayingId,
                            isPlayingNow = playerState.isPlaying,
                            coverUrl = { track, size -> viewModel.coverUrl(track.id, track.hasCover, size) },
                            artistImageUrl = viewModel::artistImageUrl,
                            actions = LibraryActions(
                                onSelectTab = selectTab,
                                onQueryChange = viewModel::setQuery,
                                onToggleChip = viewModel::toggleChip,
                                onShowChip = viewModel::showOnlyChip,
                                onClearChips = viewModel::clearChips,
                                onSetSort = viewModel::setSort,
                                onSetSortAscending = viewModel::setSortAscending,
                                onSetAlbumSort = viewModel::setAlbumSort,
                                onToggleUnreleased = viewModel::toggleUnreleasedOnly,
                                onSetArtistMode = viewModel::setArtistMode,
                                onOpenArtist = { viewModel.openArtist(it.label) },
                                onClearArtistFilter = viewModel::clearArtistFilter,
                                onOpenAlbum = viewModel::openAlbum,
                                onOpenAlbumKey = viewModel::openAlbumKey,
                                onOpenArtistName = viewModel::openArtist,
                                onToggleLike = viewModel::toggleLike,
                                onPlay = playFromTop,
                                onActivateRow = activateRow,
                                onShuffle = shuffle,
                                onRefresh = viewModel::refresh,
                                onUnpair = viewModel::unpair,
                                onSwitchAccount = viewModel::switchAccount,
                                onAddAccount = viewModel::beginAddAccount,
                            ),
                            contentPadding = PaddingValues(bottom = 12.dp),
                            listStates = listStates,
                        )
                    }

                    // Inside the content box, so it always lands just above whatever is docked
                    // under it — mini player, navigation bar, both or neither — with no offset to
                    // keep in step when one of them comes or goes. While the player is up the
                    // player draws it instead (see below): down here it would be under the sheet.
                    if (!nowPlayingVisible) {
                        SnackbarHost(
                            hostState = snackbarHost,
                            modifier = Modifier.align(Alignment.BottomCenter).padding(bottom = 8.dp),
                        )
                    }
                }

                // Docked directly above the navigation bar, the web's compact mini player position.
                if (playerState.isActive) {
                    MiniPlayer(
                        state = playerState,
                        // From the queue item itself, never rebuilt from the paired routes — a
                        // share queue's covers live on the sharing server, and unpaired there is
                        // no route.
                        coverUrl = playerState.artworkUrl,
                        onExpand = { showNowPlaying = true },
                        onPlayPause = viewModel::togglePlayPause,
                        onNext = viewModel::next,
                        devices = devices,
                        modifier = Modifier.padding(top = 4.dp, bottom = 8.dp),
                    )
                }
                if (showTabs && !useRail) {
                    // The bar takes the navigation-bar inset itself (see LibraryNavigationBar).
                    LibraryNavigationBar(selected = ui.tab, onSelect = onTabBar)
                } else {
                    Spacer(Modifier.navigationBarsPadding())
                }
            }
        }

        AnimatedVisibility(
            visible = nowPlayingVisible,
            // The web's presentation curve, which Now Playing rises and falls on (and a drag that
            // does not make it springs back on): 350ms, `cubic-bezier(0.32, 0.72, 0, 1)`.
            enter = slideInVertically(tween(PRESENT_MS, easing = EasePresent)) { it },
            exit = slideOutVertically(tween(PRESENT_MS, easing = EasePresent)) { it },
        ) {
            NowPlayingScreen(
                state = playerState,
                // The item's own artwork URL (the 640 bucket for library tracks) — see MiniPlayer.
                coverUrl = playerState.artworkUrl,
                // The ambient wash is a blown-up blur, so the full-size artwork works fine — it is
                // the same cached image the hero shows.
                ambientCoverUrl = playerState.artworkUrl,
                lyricsState = lyricsState,
                videoState = videoState,
                isLiked = !isShareQueue && playerState.trackId in likedIds,
                showVideoBackdrop = showVideoBackdrop,
                onToggleVideoBackdrop = { showVideoBackdrop = !showVideoBackdrop },
                onToggleLike = if (isShareQueue) null else {
                    { playerState.trackId?.let(viewModel::toggleLike) }
                },
                onOpenArtist = nowPlayingLinks?.let { links ->
                    { leaveForLibrary(); viewModel.openArtist(links.artist) }
                },
                onOpenAlbum = nowPlayingLinks?.let { links ->
                    { leaveForLibrary(); viewModel.openAlbumKey(links.albumKey) }
                },
                onCollapse = { showNowPlaying = false },
                // Routed by the ViewModel: this phone's player, or commands to the device holding
                // the session, or — for a remembered one — picking it up here.
                onPlayPause = viewModel::togglePlayPause,
                onNext = viewModel::next,
                onPrevious = viewModel::previous,
                onSeek = viewModel::seekTo,
                onSetSpeed = viewModel::setPlaybackSpeed,
                onToggleShuffle = viewModel.player::toggleShuffle,
                onCycleRepeat = viewModel.player::cycleRepeatMode,
                onAttachVideoSurface = viewModel.video::attachSurface,
                onDetachVideoSurface = viewModel.video::clearSurface,
                sharedBy = nowPlayingSharedBy,
                isPresented = nowPlayingVisible,
                // The one host, moved up here while the sheet covers the library's: a heart that
                // failed from the player has to say so where the listener is looking. Never both at
                // once — the library's comes back the moment the sheet starts down.
                snackbarHost = { if (nowPlayingVisible) SnackbarHost(hostState = snackbarHost) },
                devices = devices,
            )
        }
    }
}
