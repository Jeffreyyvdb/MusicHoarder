package com.musichoarder.app.ui

import android.Manifest
import android.os.Build
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.CubicBezierEasing
import androidx.compose.animation.core.tween
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.musichoarder.app.data.LibraryTab
import com.musichoarder.app.ui.theme.MhTheme

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
    val playerState by viewModel.player.state.collectAsStateWithLifecycle()
    val pairError by viewModel.pairError.collectAsStateWithLifecycle()
    val lyricsState by viewModel.lyrics.collectAsStateWithLifecycle()
    val videoState by viewModel.video.state.collectAsStateWithLifecycle()
    val pendingPairingHost by viewModel.pendingPairingHost.collectAsStateWithLifecycle()
    val likedIds by viewModel.likedIds.collectAsStateWithLifecycle()
    val share by viewModel.share.collectAsStateWithLifecycle()
    val invite by viewModel.invite.collectAsStateWithLifecycle()
    val isShareQueue by viewModel.isShareQueue.collectAsStateWithLifecycle()

    // Saveable, not remembered: a rotation or a trip through process death used to drop the open
    // player. The open album moved into the ViewModel with the rest of the library's view state.
    var showNowPlaying by rememberSaveable { mutableStateOf(false) }
    var showVideoBackdrop by rememberSaveable { mutableStateOf(true) }
    val snackbarHost = remember { SnackbarHostState() }

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
    // an App Link must never dead-end on the pairing screen.
    if (session == null && share == null && invite == null) {
        val emailSentTo by viewModel.emailLinkSentTo.collectAsStateWithLifecycle()
        PairScreen(
            error = pairError,
            emailSentTo = emailSentTo,
            onScanned = viewModel::pairFromCode,
            onManual = viewModel::pairManually,
            onRequestEmailLink = viewModel::requestEmailLink,
            onError = viewModel::setPairError,
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

    Box(modifier = modifier.fillMaxSize().background(MhTheme.colors.background)) {
        Column(modifier = Modifier.fillMaxSize()) {
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
                        playingTrackId = playerState.trackId,
                        isPlayingNow = playerState.isPlaying,
                        onPlay = { tracks, index ->
                            viewModel.playShare(tracks, index)
                            showNowPlaying = true
                        },
                        onShuffle = { tracks ->
                            viewModel.playShare(tracks.shuffled(), 0)
                            showNowPlaying = true
                        },
                        onClose = viewModel::closeShare,
                        onRetry = viewModel::retryShare,
                        contentPadding = PaddingValues(bottom = 12.dp),
                    )
                } else if (album != null) {
                    AlbumScreen(
                        album = album,
                        coverUrl = { track, size -> viewModel.coverUrl(track.id, track.hasCover, size) },
                        playingTrackId = playerState.trackId,
                        likes = likes,
                        onToggleLike = viewModel::toggleLike,
                        onPlay = { tracks, index ->
                            viewModel.play(tracks, index)
                            showNowPlaying = true
                        },
                        onShuffle = { tracks ->
                            viewModel.play(tracks.shuffled(), 0)
                            showNowPlaying = true
                        },
                        onBack = viewModel::closeAlbum,
                        contentPadding = PaddingValues(bottom = 12.dp),
                    )
                } else {
                    LibraryShell(
                        state = library,
                        ui = ui,
                        content = content,
                        accounts = accounts,
                        albumStatuses = albumStatuses,
                        likes = likes,
                        playingTrackId = playerState.trackId,
                        isPlayingNow = playerState.isPlaying,
                        coverUrl = { track, size -> viewModel.coverUrl(track.id, track.hasCover, size) },
                        artistImageUrl = viewModel::artistImageUrl,
                        actions = LibraryActions(
                            onSelectTab = viewModel::selectTab,
                            onQueryChange = viewModel::setQuery,
                            onToggleChip = viewModel::toggleChip,
                            onClearChips = viewModel::clearChips,
                            onSetSort = viewModel::setSort,
                            onSetAlbumSort = viewModel::setAlbumSort,
                            onToggleUnreleased = viewModel::toggleUnreleasedOnly,
                            onSetArtistMode = viewModel::setArtistMode,
                            onSetLetter = viewModel::setLetter,
                            onOpenArtist = { viewModel.openArtist(it.label) },
                            onClearArtistFilter = viewModel::clearArtistFilter,
                            onOpenAlbum = viewModel::openAlbum,
                            onToggleLike = viewModel::toggleLike,
                            onPlay = { tracks, index ->
                                viewModel.play(tracks, index)
                                showNowPlaying = true
                            },
                            onShuffle = { tracks ->
                                viewModel.play(tracks.shuffled(), 0)
                                showNowPlaying = true
                            },
                            onRefresh = viewModel::refresh,
                            onUnpair = viewModel::unpair,
                            onSwitchAccount = viewModel::switchAccount,
                            onAddAccountScanned = viewModel::pairFromCode,
                            onScanError = viewModel::reportPairProblem,
                        ),
                        contentPadding = PaddingValues(bottom = 12.dp),
                    )
                }
            }

            // The bar floats clear of the bottom edge the way the web's does, so it reads as
            // chrome sitting over the list rather than a docked toolbar.
            if (playerState.isActive) {
                MiniPlayer(
                    state = playerState,
                    // From the queue item itself, never rebuilt from the paired routes — a share
                    // queue's covers live on the sharing server, and unpaired there is no route.
                    coverUrl = playerState.artworkUrl,
                    onExpand = { showNowPlaying = true },
                    onPlayPause = viewModel.player::togglePlayPause,
                    onNext = viewModel.player::next,
                    modifier = Modifier.padding(bottom = 10.dp),
                )
            }
            Spacer(Modifier.navigationBarsPadding())
        }

        SnackbarHost(
            hostState = snackbarHost,
            modifier = Modifier
                .align(Alignment.BottomCenter)
                .navigationBarsPadding()
                .padding(bottom = 76.dp),
        )

        AnimatedVisibility(
            visible = showNowPlaying && playerState.isActive,
            // The web's house curve for punchy chrome — expo-out, 280ms.
            enter = slideInVertically(tween(280, easing = SheetEasing)) { it },
            exit = slideOutVertically(tween(220, easing = SheetEasing)) { it },
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
                onCollapse = { showNowPlaying = false },
                onPlayPause = viewModel.player::togglePlayPause,
                onNext = viewModel.player::next,
                onPrevious = viewModel.player::previous,
                onSeek = { positionMs ->
                    viewModel.player.seekTo(positionMs)
                    viewModel.video.onSeek(positionMs)
                },
                onSetSpeed = viewModel::setPlaybackSpeed,
                onToggleShuffle = viewModel.player::toggleShuffle,
                onCycleRepeat = viewModel.player::cycleRepeatMode,
                onAttachVideoSurface = viewModel.video::attachSurface,
                onDetachVideoSurface = viewModel.video::clearSurface,
            )
        }
    }
}

/** `cubic-bezier(0.23, 1, 0.32, 1)` — the easing the web's floating chrome enters on. */
private val SheetEasing = CubicBezierEasing(0.23f, 1f, 0.32f, 1f)
