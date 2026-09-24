package com.musichoarder.app.ui

import android.app.Activity
import android.content.Context
import android.content.ContextWrapper
import android.provider.Settings
import android.view.TextureView
import androidx.compose.animation.Crossfade
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.CubicBezierEasing
import androidx.compose.animation.core.animate
import androidx.compose.animation.core.animateDpAsState
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.Orientation
import androidx.compose.foundation.gestures.draggable
import androidx.compose.foundation.gestures.rememberDraggableState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material.icons.automirrored.rounded.KeyboardArrowRight
import androidx.compose.material.icons.rounded.Album
import androidx.compose.material.icons.rounded.Check
import androidx.compose.material.icons.rounded.Favorite
import androidx.compose.material.icons.rounded.FavoriteBorder
import androidx.compose.material.icons.rounded.Group
import androidx.compose.material.icons.rounded.KeyboardArrowDown
import androidx.compose.material.icons.rounded.Lyrics
import androidx.compose.material.icons.rounded.MoreVert
import androidx.compose.material.icons.rounded.OndemandVideo
import androidx.compose.material.icons.rounded.Person
import androidx.compose.material.icons.rounded.Repeat
import androidx.compose.material.icons.rounded.RepeatOne
import androidx.compose.material.icons.rounded.Shuffle
import androidx.compose.material.icons.rounded.Speed
import androidx.compose.material.icons.rounded.Wallpaper
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.LayoutCoordinates
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.role
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.semantics.toggleableState
import androidx.compose.ui.state.ToggleableState
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.LinkAnnotation
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.TextLinkStyles
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.withLink
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.view.WindowCompat
import androidx.media3.common.Player
import com.musichoarder.app.player.PlayerUiState
import com.musichoarder.app.player.VideoState
import com.musichoarder.app.ui.theme.LocalMhColors
import com.musichoarder.app.ui.theme.MhColors
import com.musichoarder.app.ui.theme.MhMediaTokens
import com.musichoarder.app.ui.theme.MhMenuShape
import com.musichoarder.app.ui.theme.MhTheme
import com.musichoarder.app.ui.theme.MusicHoarderTheme
import com.musichoarder.app.ui.theme.mediaAppearance
import com.musichoarder.app.ui.theme.mediaTokens
import kotlin.math.roundToInt

/**
 * What the middle of the player is showing — the web's Now Playing *modes*. Only the middle swaps;
 * the scrubber, the transport and the bottom row keep one home underneath, so the play button is
 * never somewhere else because you looked at the lyrics.
 */
private enum class PlayerPane { Song, Lyrics, Video }

/**
 * The full-screen player — the web's Now Playing (`SongDetailHost.svelte` + `TrackPanel.svelte`),
 * in the same iPhone shape: a grabber, ⌄ and ⋮ across the top; the artwork and title in the middle;
 * then the scrubber with its times, the 56/72/56dp transport, and a row of toggles —
 * Lyrics · Video (when there is a clip) · Shuffle · Repeat. Lyrics and Video swap the artwork for
 * the words or the clip, and fold the song into a compact header in the top bar.
 *
 * It has its own **media appearance**, dark whatever the app theme, as on the web: white text over
 * a blurred wash of the cover, dimmed per cover so the text keeps its contrast (`CoverDim.kt`,
 * `MediaColors.kt`). Its menus open in the plain dark palette on their solid popover.
 *
 * The music video is a *backdrop* by default, exactly as on the web: muted, behind everything,
 * slaved to the audio clock. The Video toggle promotes it into the middle, letterboxed.
 *
 * Drag it down to dismiss — from the top bar or the artwork — as well as the chevron and Back.
 *
 * Deliberately one-sided: the web's **Info** page (its bottom row's Info, and "Song info" in a
 * row's ⋯ menu) has no Android counterpart. It is mostly curation over the pipeline's data —
 * provenance, enrichment, fingerprint, Reset metadata, the timeline — which the phone does not
 * carry, so its slot in the bottom row holds Shuffle and Repeat instead, and playback speed and
 * "Show video as background" live in the ⋮ menu (PlayerMoreMenu).
 */
@Composable
fun NowPlayingScreen(
    state: PlayerUiState,
    coverUrl: String?,
    ambientCoverUrl: String?,
    lyricsState: LyricsUiState,
    videoState: VideoState,
    isLiked: Boolean,
    showVideoBackdrop: Boolean,
    onToggleVideoBackdrop: () -> Unit,
    /** Null hides the heart — the share queue's foreign ids have nothing to like. */
    onToggleLike: (() -> Unit)?,
    /**
     * The `artist · album` line's two destinations. Null on either leaves that half plain text —
     * see `resolveNowPlayingLinks`, which decides when this library can answer for the track.
     * Both collapse the player on the way, so the tap lands on a page you can see.
     */
    onOpenArtist: (() -> Unit)?,
    onOpenAlbum: (() -> Unit)?,
    onCollapse: () -> Unit,
    onPlayPause: () -> Unit,
    onNext: () -> Unit,
    onPrevious: () -> Unit,
    onSeek: (Long) -> Unit,
    onSetSpeed: (Float) -> Unit,
    onToggleShuffle: () -> Unit,
    onCycleRepeat: () -> Unit,
    onAttachVideoSurface: (TextureView) -> Unit,
    onDetachVideoSurface: () -> Unit,
    modifier: Modifier = Modifier,
    /** "Shared by X" when someone granted this song, the web's `SharedByBadge`; null for your own. */
    sharedBy: String? = null,
    /**
     * Whether the player is (still, or again) on its way up. A drag-dismissed sheet is left where
     * the finger let go for the exit slide to carry on from; if it is brought back before that
     * slide finishes, this is what puts it back together.
     */
    isPresented: Boolean = true,
    /**
     * The app's snackbar host, drawn just above the transport while the player covers the library's
     * own — so a like that failed from the heart up here is reported where the listener is looking.
     */
    snackbarHost: @Composable () -> Unit = {},
) {
    // The media appearance: Material's dark scheme (so the menus open dark) and, over it, the
    // player's own white-on-cover tokens. The contrast palettes still apply underneath.
    MusicHoarderTheme(darkTheme = true) {
        val menuColors = MhTheme.colors
        val media = remember(menuColors) { menuColors.mediaAppearance() }
        val tokens = remember(menuColors.isHighContrast) { mediaTokens(menuColors.isHighContrast) }
        CompositionLocalProvider(LocalMhColors provides media) {
            PlayerLayout(
                state = state,
                coverUrl = coverUrl,
                ambientCoverUrl = ambientCoverUrl,
                lyricsState = lyricsState,
                videoState = videoState,
                isLiked = isLiked,
                sharedBy = sharedBy,
                isPresented = isPresented,
                showVideoBackdrop = showVideoBackdrop,
                tokens = tokens,
                menuColors = menuColors,
                onToggleVideoBackdrop = onToggleVideoBackdrop,
                onToggleLike = onToggleLike,
                onOpenArtist = onOpenArtist,
                onOpenAlbum = onOpenAlbum,
                onCollapse = onCollapse,
                onPlayPause = onPlayPause,
                onNext = onNext,
                onPrevious = onPrevious,
                onSeek = onSeek,
                onSetSpeed = onSetSpeed,
                onToggleShuffle = onToggleShuffle,
                onCycleRepeat = onCycleRepeat,
                onAttachVideoSurface = onAttachVideoSurface,
                onDetachVideoSurface = onDetachVideoSurface,
                snackbarHost = snackbarHost,
                modifier = modifier,
            )
        }
    }
}

@Composable
private fun PlayerLayout(
    state: PlayerUiState,
    coverUrl: String?,
    ambientCoverUrl: String?,
    lyricsState: LyricsUiState,
    videoState: VideoState,
    isLiked: Boolean,
    sharedBy: String?,
    isPresented: Boolean,
    showVideoBackdrop: Boolean,
    tokens: MhMediaTokens,
    menuColors: MhColors,
    onToggleVideoBackdrop: () -> Unit,
    onToggleLike: (() -> Unit)?,
    onOpenArtist: (() -> Unit)?,
    onOpenAlbum: (() -> Unit)?,
    onCollapse: () -> Unit,
    onPlayPause: () -> Unit,
    onNext: () -> Unit,
    onPrevious: () -> Unit,
    onSeek: (Long) -> Unit,
    onSetSpeed: (Float) -> Unit,
    onToggleShuffle: () -> Unit,
    onCycleRepeat: () -> Unit,
    onAttachVideoSurface: (TextureView) -> Unit,
    onDetachVideoSurface: () -> Unit,
    snackbarHost: @Composable () -> Unit,
    modifier: Modifier,
) {
    // Two different questions: is there still a clip to offer a toggle for, and is one running
    // right now? Switching the backdrop off stops the clip, and must not take the Video toggle
    // with it.
    val watchable = videoState.isWatchable
    var pane by rememberSaveable { mutableStateOf(PlayerPane.Song) }
    // The track whose default mode has already been decided (or overridden by a tap).
    var settledTrackId by rememberSaveable { mutableStateOf<Int?>(null) }

    // A song without a video must not strand the screen in a watch view it can no longer show.
    if (pane == PlayerPane.Video && !watchable) pane = PlayerPane.Song

    // The web opens on Lyrics when the track has any, else on the artwork — and only ever on a
    // song *change*, so a manual switch is never clobbered. Lyrics arrive asynchronously here, so
    // the decision waits for the fetch to answer and a tap settles the track early.
    LaunchedEffect(state.trackId, lyricsState) {
        val id = state.trackId ?: return@LaunchedEffect
        if (id == settledTrackId || lyricsState is LyricsUiState.Loading) return@LaunchedEffect
        settledTrackId = id
        pane = if (lyricsState.hasLyrics) PlayerPane.Lyrics else PlayerPane.Song
    }

    val choose: (PlayerPane) -> Unit = { target ->
        settledTrackId = state.trackId
        // You cannot watch the video with the video switched off.
        if (target == PlayerPane.Video && !showVideoBackdrop) onToggleVideoBackdrop()
        pane = target
    }
    // The bottom row's Lyrics and Video are toggles: pressing the active one goes back to the art.
    val toggle: (PlayerPane) -> Unit = { target -> choose(if (pane == target) PlayerPane.Song else target) }

    val watching = pane == PlayerPane.Video
    val clipRunning = videoState.isVisible && showVideoBackdrop
    // Chrome painted over a moving clip needs the web's text-shadow halo to stay readable. In the
    // watch view the clip sits in the middle and the chrome is back over the still, dimmed wash.
    val onSurface = clipRunning && !watching
    val dimAlpha by animateFloatAsState(
        targetValue = rememberCoverDimAlpha(ambientCoverUrl),
        animationSpec = tween(300),
        label = "cover-dim",
    )

    LightSystemBarIcons()

    // ── Drag to dismiss ────────────────────────────────────────────────────────────────────────
    // From the top bar and the artwork, as on the web: the sheet follows the finger, and on
    // release it goes past a quarter of its height or on a flick, else springs back on the
    // presentation curve. Under Remove animations the drag fades instead of moving.
    var dragOffset by remember { mutableFloatStateOf(0f) }
    var sheetHeight by remember { mutableFloatStateOf(0f) }
    val reducedMotion = rememberReducedMotion()
    val dismissVelocity = with(LocalDensity.current) { DISMISS_VELOCITY_DP_PER_S.dp.toPx() }
    val collapse by rememberUpdatedState(onCollapse)
    val dragState = rememberDraggableState { delta -> dragOffset = (dragOffset + delta).coerceAtLeast(0f) }
    LaunchedEffect(isPresented) { if (isPresented) dragOffset = 0f }
    val dismissDrag = Modifier.draggable(
        state = dragState,
        orientation = Orientation.Vertical,
        onDragStopped = { velocity ->
            if (dragOffset > sheetHeight * 0.25f || velocity > dismissVelocity) {
                // Leave the offset where the finger left it: the exit slide carries on from there.
                collapse()
            } else {
                animate(dragOffset, 0f, animationSpec = tween(PRESENT_MS, easing = EasePresent)) { value, _ ->
                    dragOffset = value
                }
            }
        },
    )

    // Where the watch view's clip goes: the middle of the screen, measured against the screen's
    // own box so the drag and the slide-in (which move both) cancel out.
    val screenCoordinates = remember { CoordinatesHolder() }
    var videoStage by remember { mutableStateOf<Rect?>(null) }

    Box(
        modifier = modifier
            .fillMaxSize()
            .onSizeChanged { sheetHeight = it.height.toFloat() }
            .graphicsLayer {
                val progress = if (sheetHeight > 0f) (dragOffset / sheetHeight).coerceIn(0f, 1f) else 0f
                if (reducedMotion) alpha = 1f - progress else translationY = dragOffset
            }
            .background(Color.Black)
            .onGloballyPositioned { screenCoordinates.value = it },
    ) {
        PlayerBackdrop(
            ambientCoverUrl = ambientCoverUrl,
            state = state,
            videoState = videoState,
            dimAlpha = dimAlpha,
            showVideo = clipRunning,
            watching = watching,
            stage = videoStage,
            onAttachVideoSurface = onAttachVideoSurface,
            onDetachVideoSurface = onDetachVideoSurface,
        )

        // Insets go on the chrome, not on the screen: the wash and the clip have to reach the very
        // edges, or the window's own background shows as bars top and bottom. The chrome itself
        // stops at the web's `max-w-xl`, centred, so a phone on its side or a tablet does not
        // spread the transport across the whole width.
        Column(
            modifier = Modifier
                .align(Alignment.TopCenter)
                .fillMaxHeight()
                .widthIn(max = MAX_CONTENT_WIDTH)
                .fillMaxWidth()
                .statusBarsPadding()
                .navigationBarsPadding(),
        ) {
            PlayerTopBar(
                state = state,
                coverUrl = coverUrl,
                condensed = pane != PlayerPane.Song,
                isLiked = isLiked,
                tokens = tokens,
                onToggleLike = onToggleLike,
                onShowArtwork = { choose(PlayerPane.Song) },
                onClose = onCollapse,
                modifier = dismissDrag,
            ) {
                PlayerMoreMenu(
                    menuColors = menuColors,
                    rate = state.playbackRate,
                    onSetSpeed = onSetSpeed,
                    onOpenAlbum = onOpenAlbum,
                    onOpenArtist = onOpenArtist,
                    // On the watch view the clip is the point, so there is nothing to switch off there.
                    showBackgroundToggle = watchable && !watching,
                    showVideoBackdrop = showVideoBackdrop,
                    onToggleVideoBackdrop = onToggleVideoBackdrop,
                )
            }

            Box(modifier = Modifier.weight(1f).fillMaxWidth()) {
                Crossfade(targetState = pane, animationSpec = tween(220), label = "player-pane") { current ->
                    when (current) {
                        PlayerPane.Song -> SongPane(
                            state = state,
                            coverUrl = coverUrl,
                            isLiked = isLiked,
                            sharedBy = sharedBy,
                            onSurface = onSurface,
                            tokens = tokens,
                            onToggleLike = onToggleLike,
                            onOpenArtist = onOpenArtist,
                            onOpenAlbum = onOpenAlbum,
                            modifier = dismissDrag,
                        )

                        PlayerPane.Lyrics -> LyricsView(
                            state = lyricsState,
                            positionMs = state.positionMs,
                            onSeek = onSeek,
                            modifier = Modifier.fillMaxSize().padding(horizontal = 24.dp),
                        )

                        // The clip itself is drawn by the backdrop, which keeps the surface mounted;
                        // this only says where the letterbox goes.
                        PlayerPane.Video -> Box(
                            modifier = Modifier
                                .fillMaxSize()
                                .padding(horizontal = 16.dp, vertical = 12.dp)
                                .onGloballyPositioned { stage ->
                                    videoStage = screenCoordinates.value
                                        ?.takeIf { it.isAttached && stage.isAttached }
                                        ?.localBoundingBoxOf(stage, clipBounds = false)
                                },
                        )
                    }
                }
                // Over the foot of the pane rather than the controls: a message must never land on
                // the button the listener is about to press.
                Box(modifier = Modifier.align(Alignment.BottomCenter)) { snackbarHost() }
            }

            PlayerTransport(
                state = state,
                onPlayPause = onPlayPause,
                onNext = onNext,
                onPrevious = onPrevious,
                onSeek = onSeek,
                onSetSpeed = onSetSpeed,
                onSurface = onSurface,
                menuColors = menuColors,
                modifier = Modifier.padding(horizontal = 28.dp).padding(top = 8.dp),
            )

            PlayerBottomRow(
                pane = pane,
                watchable = watchable,
                state = state,
                tokens = tokens,
                onToggleLyrics = { toggle(PlayerPane.Lyrics) },
                onToggleVideo = { toggle(PlayerPane.Video) },
                onToggleShuffle = onToggleShuffle,
                onCycleRepeat = onCycleRepeat,
            )

            state.error?.let {
                Text(
                    it,
                    style = MaterialTheme.typography.bodySmall,
                    color = MhTheme.colors.destructiveText,
                    modifier = Modifier.fillMaxWidth().padding(horizontal = 28.dp, vertical = 4.dp),
                )
            }
        }
    }
}

/**
 * The layers under the chrome: the media wash (black, the blurred cover, the per-cover dim), then
 * the clip — cropped full-bleed as a backdrop, or letterboxed into the middle [stage] when you are
 * watching it.
 */
@Composable
private fun PlayerBackdrop(
    ambientCoverUrl: String?,
    state: PlayerUiState,
    videoState: VideoState,
    dimAlpha: Float,
    showVideo: Boolean,
    watching: Boolean,
    stage: Rect?,
    onAttachVideoSurface: (TextureView) -> Unit,
    onDetachVideoSurface: () -> Unit,
) {
    // Always mounted as the base: a clip paints nothing until its first frame is decoded (and
    // again after a resync seek into an unbuffered range), and without it those windows flash black.
    AmbientBackdrop(
        url = ambientCoverUrl,
        artist = state.artist,
        title = state.album.ifBlank { state.title },
        dimAlpha = dimAlpha,
        modifier = Modifier.fillMaxSize(),
    )

    if (!showVideo) return

    // The surface stays mounted whenever a clip is running, so the decoder is not torn down every
    // time the mode changes and the first frame is already there when it is promoted. Only its box
    // and fit change: cropped to fill behind the player, fitted into the middle when watching.
    val density = LocalDensity.current
    val fitted = if (watching && stage != null) fitClip(stage, videoState.aspectRatio) else null
    PlayerVideoLayer(
        aspectRatio = videoState.aspectRatio,
        crop = !watching,
        onAttach = onAttachVideoSurface,
        onDetach = onDetachVideoSurface,
        modifier = if (fitted == null) {
            Modifier.fillMaxSize()
        } else {
            Modifier
                .offset { IntOffset(fitted.left.roundToInt(), fitted.top.roundToInt()) }
                .size(with(density) { fitted.width.toDp() }, with(density) { fitted.height.toDp() })
                .shadow(24.dp, VideoShape, ambientColor = Color.Black, spotColor = Color.Black)
                .clip(VideoShape)
        },
    )

    if (!watching) {
        // As a backdrop the clip is atmosphere and legibility wins, so it sits under the web's
        // vertical wash — heaviest at the top and foot, where the chrome lives.
        Box(
            modifier = Modifier.fillMaxSize().background(
                Brush.verticalGradient(
                    0f to Color.Black.copy(alpha = 0.75f),
                    0.5f to Color.Black.copy(alpha = 0.45f),
                    1f to Color.Black.copy(alpha = 0.85f),
                )
            )
        )
    }
}

/** The clip's letterbox inside [stage]: `videoChildSize`'s fit, centred. Unknown ratio fills it. */
private fun fitClip(stage: Rect, aspectRatio: Float?): Rect {
    val (width, height) = videoChildSize(
        stage.width.roundToInt(),
        stage.height.roundToInt(),
        aspectRatio ?: 0f,
        crop = false,
    )
    val left = stage.left + (stage.width - width) / 2f
    val top = stage.top + (stage.height - height) / 2f
    return Rect(left, top, left + width, top + height)
}

/**
 * The grabber, ⌄ and ⋮ — and, outside the artwork, the song condensed into the bar (art, title,
 * artist, heart), which a tap takes back to the artwork. The whole bar is a drag-to-dismiss zone.
 */
@Composable
private fun PlayerTopBar(
    state: PlayerUiState,
    coverUrl: String?,
    condensed: Boolean,
    isLiked: Boolean,
    tokens: MhMediaTokens,
    onToggleLike: (() -> Unit)?,
    onShowArtwork: () -> Unit,
    onClose: () -> Unit,
    modifier: Modifier = Modifier,
    menu: @Composable () -> Unit,
) {
    val colors = MhTheme.colors
    Column(modifier = modifier.fillMaxWidth()) {
        // The grabber says "this pulls down"; a tap on it closes too. Hidden from TalkBack, which
        // has the chevron right under it.
        Box(modifier = Modifier.align(Alignment.CenterHorizontally).clearAndSetSemantics { }) {
            Box(
                modifier = Modifier
                    .size(width = 96.dp, height = 20.dp)
                    .clickable(interactionSource = null, indication = null, onClick = onClose),
                contentAlignment = Alignment.Center,
            ) {
                Box(
                    Modifier
                        .size(width = 36.dp, height = 5.dp)
                        .clip(CircleShape)
                        .background(tokens.grabber)
                )
            }
        }
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .heightIn(min = 56.dp)
                .padding(horizontal = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            IconButton(onClick = onClose) {
                Icon(
                    Icons.Rounded.KeyboardArrowDown,
                    contentDescription = "Close player",
                    tint = colors.foreground,
                    modifier = Modifier.size(32.dp),
                )
            }
            if (condensed) {
                Row(
                    modifier = Modifier
                        .weight(1f)
                        .clip(RoundedCornerShape(10.dp))
                        .clickable(onClickLabel = "Show the artwork", onClick = onShowArtwork)
                        .padding(horizontal = 4.dp, vertical = 4.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Artwork(
                        url = coverUrl,
                        artist = state.artist,
                        title = state.album.ifBlank { state.title },
                        shape = RoundedCornerShape(6.dp),
                        modifier = Modifier
                            .size(44.dp)
                            .shadow(4.dp, RoundedCornerShape(6.dp), ambientColor = Color.Black, spotColor = Color.Black),
                    )
                    Spacer(Modifier.width(12.dp))
                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                            state.title,
                            style = MaterialTheme.typography.titleLarge,
                            color = colors.foreground,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                        Text(
                            state.artist,
                            style = MaterialTheme.typography.bodyMedium,
                            color = colors.mutedForeground,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }
                }
                if (onToggleLike != null) {
                    PlayerHeart(isLiked = isLiked, liked = tokens.liked, iconSize = 22.dp, onClick = onToggleLike)
                }
            } else {
                Spacer(Modifier.weight(1f))
            }
            menu()
        }
    }
}

/**
 * The artwork and the title block — also a drag-to-dismiss zone, as in Apple Music. The art is as
 * large as fits over the title, capped where a phone's width stops being the limit.
 */
@Composable
private fun SongPane(
    state: PlayerUiState,
    coverUrl: String?,
    isLiked: Boolean,
    sharedBy: String?,
    onSurface: Boolean,
    tokens: MhMediaTokens,
    onToggleLike: (() -> Unit)?,
    onOpenArtist: (() -> Unit)?,
    onOpenAlbum: (() -> Unit)?,
    modifier: Modifier = Modifier,
) {
    val colors = MhTheme.colors
    BoxWithConstraints(
        modifier = modifier.fillMaxSize().padding(horizontal = 32.dp),
        contentAlignment = Alignment.Center,
    ) {
        // A phone on its side leaves the middle shorter than any useful cover plus the title; the
        // title (and its links and heart) matter more there, so the art steps aside.
        val showArt = maxHeight >= MIN_ART + TITLE_BLOCK_ALLOWANCE
        val artSize = minOf(maxWidth, maxHeight - TITLE_BLOCK_ALLOWANCE, MAX_ART).coerceAtLeast(MIN_ART)
        Column(modifier = Modifier.width(if (showArt) artSize else maxWidth)) {
            if (showArt) {
                HeroCover(state = state, coverUrl = coverUrl, modifier = Modifier.size(artSize))
                Spacer(Modifier.height(28.dp))
            }
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        text = state.title,
                        style = legible(HeroTitleStyle, onSurface),
                        color = colors.foreground,
                        // One line, like the web's `truncate`: a long "(feat. ...)" title would
                        // otherwise push the cover around from track to track.
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                    TrackSubtitle(
                        artist = state.artist,
                        album = state.album,
                        onSurface = onSurface,
                        onOpenArtist = onOpenArtist,
                        onOpenAlbum = onOpenAlbum,
                    )
                }
                if (onToggleLike != null) {
                    PlayerHeart(isLiked = isLiked, liked = tokens.liked, iconSize = 26.dp, onClick = onToggleLike)
                }
            }
            if (sharedBy != null) {
                Spacer(Modifier.height(10.dp))
                SharedByChip(sharedBy)
            }
        }
    }
}

/**
 * The artwork as the hero: full size while the song plays, settling back to 85% with a lighter
 * shadow while it is paused — how Apple Music says "paused" without another glyph. 400ms on the
 * presentation curve; under Remove animations Compose runs it as a cut, like the web's Reduce
 * Motion clamp does.
 */
@Composable
private fun HeroCover(state: PlayerUiState, coverUrl: String?, modifier: Modifier = Modifier) {
    // A stall or the gap between two tracks is not a pause: the player is buffering towards playing,
    // and shrinking the art on every track change would read as a hiccup.
    val playing = state.isPlaying || state.isBuffering
    val scale by animateFloatAsState(
        targetValue = if (playing) 1f else 0.85f,
        animationSpec = tween(400, easing = EasePresent),
        label = "hero-scale",
    )
    val elevation by animateDpAsState(
        targetValue = if (playing) 28.dp else 12.dp,
        animationSpec = tween(400, easing = EasePresent),
        label = "hero-shadow",
    )
    Artwork(
        url = coverUrl,
        artist = state.artist,
        title = state.album.ifBlank { state.title },
        shape = HeroShape,
        modifier = modifier
            .graphicsLayer {
                scaleX = scale
                scaleY = scale
            }
            .shadow(elevation, HeroShape, clip = false, ambientColor = Color.Black, spotColor = Color.Black),
    )
}

/** The heart: white outline at rest, the media appearance's own green when liked. */
@Composable
private fun PlayerHeart(isLiked: Boolean, liked: Color, iconSize: Dp, onClick: () -> Unit) {
    IconButton(onClick = onClick) {
        Icon(
            if (isLiked) Icons.Rounded.Favorite else Icons.Rounded.FavoriteBorder,
            contentDescription = if (isLiked) "Remove from favourites" else "Add to favourites",
            tint = if (isLiked) liked else MhTheme.colors.foreground,
            modifier = Modifier.size(iconSize),
        )
    }
}

/** The web's `SharedByBadge`: whose song this is, on the media appearance's white capsule. */
@Composable
private fun SharedByChip(label: String) {
    val colors = MhTheme.colors
    Row(
        modifier = Modifier
            .clip(CircleShape)
            .background(colors.secondary)
            .padding(horizontal = 10.dp, vertical = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(
            Icons.Rounded.Group,
            contentDescription = null,
            tint = colors.mutedForeground,
            modifier = Modifier.size(14.dp),
        )
        Spacer(Modifier.width(6.dp))
        Text(label, style = MaterialTheme.typography.labelLarge, color = colors.mutedForeground, maxLines = 1)
    }
}

/**
 * `artist · album`, under the title.
 *
 * Both halves are links into the library, as they are on the web. One [Text] rather than a row of
 * two, so the line still truncates as a whole the way the web's `truncate` does — the separator
 * stays outside both links, so it reads as two targets rather than one underlined blob.
 */
@Composable
private fun TrackSubtitle(
    artist: String,
    album: String,
    onSurface: Boolean,
    onOpenArtist: (() -> Unit)?,
    onOpenAlbum: (() -> Unit)?,
) {
    val colors = MhTheme.colors
    val text = buildAnnotatedString {
        appendNavigable(artist, colors.mutedForeground, colors.foreground, onOpenArtist)
        if (album.isNotBlank()) {
            withStyle(SpanStyle(color = colors.mutedForeground)) { append(" · ") }
            appendNavigable(album, colors.mutedForeground, colors.foreground, onOpenAlbum)
        }
    }
    Text(
        text = text,
        style = legible(MaterialTheme.typography.bodyLarge, onSurface),
        maxLines = 1,
        overflow = TextOverflow.Ellipsis,
    )
}

/**
 * One half of the subtitle: plain text when there is nowhere to go, a link when there is.
 *
 * The web leaves these undecorated and signals them on hover, which a finger has no equivalent of —
 * an unmarked tap target here would simply never be found. So the underline sits at rest and the
 * press brightens the text to `foreground`, which is the state the web's `hover:` pair produces.
 */
private fun AnnotatedString.Builder.appendNavigable(
    text: String,
    color: Color,
    pressedColor: Color,
    onClick: (() -> Unit)?,
) {
    if (onClick == null) {
        withStyle(SpanStyle(color = color)) { append(text) }
        return
    }
    val link = LinkAnnotation.Clickable(
        tag = text,
        styles = TextLinkStyles(
            style = SpanStyle(color = color, textDecoration = TextDecoration.Underline),
            pressedStyle = SpanStyle(color = pressedColor, textDecoration = TextDecoration.Underline),
        ),
    ) { onClick() }
    withLink(link) { append(text) }
}

/**
 * Lyrics · Video · Shuffle · Repeat — the web's bottom row, where its Info and AirPlay slots hold
 * Android's queue modes instead. The web transport has no shuffle or repeat at all; they work on
 * the phone and must not regress, so they take the row's spare places rather than crowding the
 * transport. An active toggle is its glyph on a white capsule, never colour alone.
 */
@Composable
private fun PlayerBottomRow(
    pane: PlayerPane,
    watchable: Boolean,
    state: PlayerUiState,
    tokens: MhMediaTokens,
    onToggleLyrics: () -> Unit,
    onToggleVideo: () -> Unit,
    onToggleShuffle: () -> Unit,
    onCycleRepeat: () -> Unit,
) {
    Row(
        modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp),
        horizontalArrangement = Arrangement.SpaceEvenly,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        PlayerToggle(
            icon = Icons.Rounded.Lyrics,
            label = "Lyrics",
            active = pane == PlayerPane.Lyrics,
            fill = tokens.fill,
            onClick = onToggleLyrics,
        )
        if (watchable) {
            PlayerToggle(
                icon = Icons.Rounded.OndemandVideo,
                label = "Video",
                active = pane == PlayerPane.Video,
                fill = tokens.fill,
                onClick = onToggleVideo,
            )
        }
        PlayerToggle(
            icon = Icons.Rounded.Shuffle,
            label = "Shuffle",
            active = state.shuffleEnabled,
            fill = tokens.fill,
            onClick = onToggleShuffle,
        )
        // Off → all → one, the cycle the phone has always had. A button with a spoken state rather
        // than a toggle, since it has three.
        val repeat = state.repeatMode
        PlayerRowButton(
            icon = if (repeat == Player.REPEAT_MODE_ONE) Icons.Rounded.RepeatOne else Icons.Rounded.Repeat,
            active = repeat != Player.REPEAT_MODE_OFF,
            fill = tokens.fill,
            modifier = Modifier
                .clickable(onClickLabel = "Change repeat mode", role = Role.Button, onClick = onCycleRepeat)
                .semantics {
                    contentDescription = "Repeat"
                    stateDescription = when (repeat) {
                        Player.REPEAT_MODE_ONE -> "One song"
                        Player.REPEAT_MODE_ALL -> "All"
                        else -> "Off"
                    }
                },
        )
    }
}

@Composable
private fun PlayerToggle(
    icon: ImageVector,
    label: String,
    active: Boolean,
    fill: Color,
    onClick: () -> Unit,
) {
    PlayerRowButton(
        icon = icon,
        active = active,
        fill = fill,
        modifier = Modifier
            .toggleable(value = active, role = Role.Checkbox, onValueChange = { onClick() })
            .semantics { contentDescription = label },
    )
}

/** A 48dp round target around the web's 44×32 capsule, which is only filled while active. */
@Composable
private fun PlayerRowButton(icon: ImageVector, active: Boolean, fill: Color, modifier: Modifier) {
    val colors = MhTheme.colors
    val ground by animateColorAsState(
        targetValue = if (active) fill else Color.Transparent,
        animationSpec = tween(200),
        label = "toggle-ground",
    )
    Box(
        // Clipped before the click, so the ripple is the round Material one, not a square.
        modifier = Modifier.size(48.dp).clip(CircleShape).then(modifier),
        contentAlignment = Alignment.Center,
    ) {
        Box(
            modifier = Modifier
                .size(width = 44.dp, height = 32.dp)
                .clip(CircleShape)
                .background(ground),
            contentAlignment = Alignment.Center,
        ) {
            Icon(
                icon,
                contentDescription = null,
                tint = if (active) colors.foreground else colors.mutedForeground,
                modifier = Modifier.size(22.dp),
            )
        }
    }
}

/**
 * The player's one ⋮ menu, the web's ⋯ reduced to what the phone can act on: Go to album / Go to
 * artist, then Playback speed › and "Show video as background". Speed is a page of the same menu
 * (the web's one submenu level) rather than a second popup stacked on the first.
 */
@Composable
private fun PlayerMoreMenu(
    menuColors: MhColors,
    rate: Float,
    onSetSpeed: (Float) -> Unit,
    onOpenAlbum: (() -> Unit)?,
    onOpenArtist: (() -> Unit)?,
    showBackgroundToggle: Boolean,
    showVideoBackdrop: Boolean,
    onToggleVideoBackdrop: () -> Unit,
) {
    var expanded by remember { mutableStateOf(false) }
    var speedPage by remember { mutableStateOf(false) }
    val close = { expanded = false }

    Box {
        IconButton(onClick = {
            speedPage = false
            expanded = true
        }) {
            Icon(Icons.Rounded.MoreVert, contentDescription = "More options", tint = MhTheme.colors.foreground)
        }
        CompositionLocalProvider(LocalMhColors provides menuColors) {
            DropdownMenu(
                expanded = expanded,
                onDismissRequest = close,
                shape = MhMenuShape,
                containerColor = menuColors.popover,
            ) {
                if (speedPage) {
                    DropdownMenuItem(
                        text = { MenuLabel("Playback speed", FontWeight.SemiBold) },
                        leadingIcon = {
                            Icon(
                                Icons.AutoMirrored.Rounded.ArrowBack,
                                contentDescription = "Back",
                                tint = menuColors.mutedForeground,
                                modifier = Modifier.size(20.dp),
                            )
                        },
                        onClick = { speedPage = false },
                    )
                    HorizontalDivider(color = menuColors.separator)
                    PlaybackSpeedItems(rate = rate, onSetSpeed = onSetSpeed, close = close)
                } else {
                    val links = listOfNotNull(
                        onOpenAlbum?.let { Triple("Go to album", Icons.Rounded.Album, it) },
                        onOpenArtist?.let { Triple("Go to artist", Icons.Rounded.Person, it) },
                    )
                    for ((label, icon, action) in links) {
                        DropdownMenuItem(
                            text = { MenuLabel(label) },
                            leadingIcon = { MenuIcon(icon) },
                            onClick = {
                                close()
                                action()
                            },
                        )
                    }
                    if (links.isNotEmpty()) HorizontalDivider(color = menuColors.separator)
                    DropdownMenuItem(
                        text = { MenuLabel("Playback speed") },
                        leadingIcon = { MenuIcon(Icons.Rounded.Speed) },
                        trailingIcon = {
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                Text(
                                    speedLabel(rate),
                                    style = MaterialTheme.typography.bodyMedium,
                                    color = menuColors.mutedForeground,
                                )
                                MenuIcon(Icons.AutoMirrored.Rounded.KeyboardArrowRight)
                            }
                        },
                        onClick = { speedPage = true },
                    )
                    if (showBackgroundToggle) {
                        // A switch, so it leaves the menu open to show the new state — the web's
                        // checkbox items do the same.
                        DropdownMenuItem(
                            text = { MenuLabel("Show video as background") },
                            leadingIcon = { MenuIcon(Icons.Rounded.Wallpaper) },
                            trailingIcon = {
                                Box(Modifier.size(20.dp)) {
                                    if (showVideoBackdrop) {
                                        Icon(Icons.Rounded.Check, contentDescription = null, tint = menuColors.primary)
                                    }
                                }
                            },
                            onClick = onToggleVideoBackdrop,
                            modifier = Modifier.semantics {
                                role = Role.Checkbox
                                toggleableState = ToggleableState(showVideoBackdrop)
                            },
                        )
                    }
                }
            }
        }
    }
}

@Composable
private fun MenuLabel(label: String, weight: FontWeight? = null) {
    Text(label, style = MaterialTheme.typography.bodyMedium, fontWeight = weight, color = MhTheme.colors.foreground)
}

@Composable
private fun MenuIcon(icon: ImageVector) {
    Icon(icon, contentDescription = null, tint = MhTheme.colors.mutedForeground, modifier = Modifier.size(20.dp))
}

/**
 * The status bar and navigation bar icons go light while the player is up: it is dark in every
 * theme, and dark icons on it would vanish. The web paints the installed app's status bar black
 * for the same reason. Restored as the player leaves.
 */
@Composable
private fun LightSystemBarIcons() {
    val view = LocalView.current
    DisposableEffect(view) {
        val window = view.context.findActivity()?.window
        val controller = window?.let { WindowCompat.getInsetsController(it, view) }
        val lightStatus = controller?.isAppearanceLightStatusBars
        val lightNavigation = controller?.isAppearanceLightNavigationBars
        controller?.isAppearanceLightStatusBars = false
        controller?.isAppearanceLightNavigationBars = false
        onDispose {
            lightStatus?.let { controller.isAppearanceLightStatusBars = it }
            lightNavigation?.let { controller.isAppearanceLightNavigationBars = it }
        }
    }
}

private tailrec fun Context.findActivity(): Activity? = when (this) {
    is Activity -> this
    is ContextWrapper -> baseContext.findActivity()
    else -> null
}

/**
 * Settings › Accessibility › Remove animations (the animator scale at 0). Compose already runs its
 * animations as cuts then; this is for the one motion it cannot see — a drag that follows the
 * finger — which fades instead, the web's Reduce Motion rule for its drags.
 */
@Composable
private fun rememberReducedMotion(): Boolean {
    val context = LocalContext.current
    return remember(context) {
        Settings.Global.getFloat(context.contentResolver, Settings.Global.ANIMATOR_DURATION_SCALE, 1f) == 0f
    }
}

/** Where the screen's box sits, read by the watch view's stage without recomposing on every move. */
private class CoordinatesHolder {
    var value: LayoutCoordinates? = null
}

/**
 * `cubic-bezier(0.32, 0.72, 0, 1)` over 350ms — the web's presentation curve (`$lib/motion.ts`),
 * which sheets and Now Playing rise, fall and spring back on.
 */
internal val EasePresent = CubicBezierEasing(0.32f, 0.72f, 0f, 1f)
internal const val PRESENT_MS = 350

/**
 * The web's `DISMISS_VELOCITY` (0.11 px/ms), a CSS px being a dp: a downward flick at least this
 * fast dismisses however short it was. A finger that rested before lifting reads as no velocity.
 */
private const val DISMISS_VELOCITY_DP_PER_S = 110f

private val HeroShape = RoundedCornerShape(12.dp)
private val VideoShape = RoundedCornerShape(14.dp)

/** `max-w-xl`: the compact layout's width, centred when the window is wider. */
private val MAX_CONTENT_WIDTH = 576.dp

/** The web's `max-w-[min(100%,40svh,329px)]`, a little larger for Android's wider phones. */
private val MAX_ART = 360.dp
private val MIN_ART = 120.dp

/** Room under the art for the title, the `artist · album` line and a Shared-by chip. */
private val TITLE_BLOCK_ALLOWANCE = 128.dp

/** `text-title-2` — the player's title: 22/28 bold. */
private val HeroTitleStyle = TextStyle(
    fontFamily = FontFamily.Default,
    fontSize = 22.sp,
    lineHeight = 28.sp,
    fontWeight = FontWeight.Bold,
    letterSpacing = (-0.3).sp,
)
