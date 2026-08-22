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
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.musichoarder.app.data.Album
import com.musichoarder.app.ui.theme.MhTheme

/**
 * The whole app in one place: pair, browse, play. Navigation is two booleans deep, which is exactly
 * as much as a first player needs — no nav graph to maintain yet.
 */
@Composable
fun MusicHoarderRoot(viewModel: AppViewModel, modifier: Modifier = Modifier) {
    val session by viewModel.session.collectAsStateWithLifecycle()
    val library by viewModel.library.collectAsStateWithLifecycle()
    val playerState by viewModel.player.state.collectAsStateWithLifecycle()
    val pairError by viewModel.pairError.collectAsStateWithLifecycle()
    val lyricsState by viewModel.lyrics.collectAsStateWithLifecycle()
    val videoState by viewModel.video.state.collectAsStateWithLifecycle()
    val pendingPairingHost by viewModel.pendingPairingHost.collectAsStateWithLifecycle()
    val likedIds by viewModel.likedIds.collectAsStateWithLifecycle()

    // Saveable, not remembered: a rotation or a trip through process death used to drop the open
    // player and whatever album was underneath it.
    var openAlbumKey by rememberSaveable { mutableStateOf<String?>(null) }
    var showNowPlaying by rememberSaveable { mutableStateOf(false) }
    var showVideoBackdrop by rememberSaveable { mutableStateOf(true) }

    // The media notification is the playback controls — without it, background playback is invisible.
    val notificationPermission = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { }
    LaunchedEffect(session) {
        if (session != null && Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
    }

    pendingPairingHost?.let { host ->
        AlertDialog(
            onDismissRequest = viewModel::dismissPendingPairingLink,
            title = { Text("Pair with a different server?") },
            text = {
                Text(
                    "This code points at $host. Pairing will replace the library this phone is " +
                        "showing now and stop playback."
                )
            },
            confirmButton = {
                TextButton(onClick = viewModel::confirmPendingPairingLink) { Text("Pair") }
            },
            dismissButton = {
                TextButton(onClick = viewModel::dismissPendingPairingLink) { Text("Cancel") }
            },
        )
    }

    if (session == null) {
        PairScreen(
            error = pairError,
            onScanned = viewModel::pairFromCode,
            onManual = viewModel::pairManually,
            onError = viewModel::setPairError,
            modifier = modifier,
        )
        return
    }

    val openAlbum: Album? = remember(openAlbumKey, library.albums) {
        library.albums.firstOrNull { it.key == openAlbumKey }
    }

    // Lyrics and the video clip are per-song extras the library dump does not carry.
    LaunchedEffect(playerState.trackId) {
        viewModel.onNowPlayingTrackChanged(playerState.trackId)
    }

    // The clip chases the audio clock, and only while the player is actually on screen — decoding
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

    BackHandler(enabled = showNowPlaying || openAlbum != null) {
        if (showNowPlaying) showNowPlaying = false else openAlbumKey = null
    }

    Box(modifier = modifier.fillMaxSize().background(MhTheme.colors.background)) {
        Column(modifier = Modifier.fillMaxSize()) {
            Box(modifier = Modifier.weight(1f)) {
                if (openAlbum != null) {
                    AlbumScreen(
                        album = openAlbum,
                        coverUrl = { track, size -> viewModel.coverUrl(track.id, track.hasCover, size) },
                        playingTrackId = playerState.trackId,
                        onPlay = { tracks, index ->
                            viewModel.play(tracks, index)
                            showNowPlaying = true
                        },
                        onShuffle = { tracks ->
                            viewModel.play(tracks.shuffled(), 0)
                            showNowPlaying = true
                        },
                        onBack = { openAlbumKey = null },
                        contentPadding = PaddingValues(bottom = 12.dp),
                    )
                } else {
                    LibraryScreen(
                        state = library,
                        coverUrl = { track, size -> viewModel.coverUrl(track.id, track.hasCover, size) },
                        playingTrackId = playerState.trackId,
                        onPlay = viewModel::play,
                        onOpenAlbum = { openAlbumKey = it.key },
                        onRefresh = viewModel::refresh,
                        onUnpair = viewModel::unpair,
                        contentPadding = PaddingValues(bottom = 12.dp),
                    )
                }
            }

            // The bar floats clear of the bottom edge the way the web's does, so it reads as
            // chrome sitting over the list rather than a docked toolbar.
            if (playerState.isActive) {
                MiniPlayer(
                    state = playerState,
                    coverUrl = viewModel.coverUrl(playerState.trackId, playerState.hasCover, 128),
                    onExpand = { showNowPlaying = true },
                    onPlayPause = viewModel.player::togglePlayPause,
                    onNext = viewModel.player::next,
                    modifier = Modifier.padding(bottom = 10.dp),
                )
            }
            Spacer(Modifier.navigationBarsPadding())
        }

        AnimatedVisibility(
            visible = showNowPlaying && playerState.isActive,
            // The web's house curve for punchy chrome — expo-out, 280ms.
            enter = slideInVertically(tween(280, easing = SheetEasing)) { it },
            exit = slideOutVertically(tween(220, easing = SheetEasing)) { it },
        ) {
            NowPlayingScreen(
                state = playerState,
                coverUrl = viewModel.coverUrl(playerState.trackId, playerState.hasCover, 640),
                // The ambient wash is a blown-up blur, so the smallest thumbnail is plenty — and on
                // API 30 and below, where there is no RenderEffect, the upscale *is* the blur.
                ambientCoverUrl = viewModel.coverUrl(playerState.trackId, playerState.hasCover, 128),
                lyricsState = lyricsState,
                videoState = videoState,
                isLiked = playerState.trackId in likedIds,
                showVideoBackdrop = showVideoBackdrop,
                onToggleVideoBackdrop = { showVideoBackdrop = !showVideoBackdrop },
                onToggleLike = { playerState.trackId?.let(viewModel::toggleLike) },
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
