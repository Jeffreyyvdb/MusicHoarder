package com.musichoarder.app.ui

import android.view.SurfaceView
import androidx.compose.animation.Crossfade
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Close
import androidx.compose.material.icons.rounded.Favorite
import androidx.compose.material.icons.rounded.FavoriteBorder
import androidx.compose.material.icons.rounded.LocalMovies
import androidx.compose.material.icons.rounded.Repeat
import androidx.compose.material.icons.rounded.RepeatOne
import androidx.compose.material.icons.rounded.Shuffle
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.media3.common.Player
import com.musichoarder.app.player.PlayerUiState
import com.musichoarder.app.player.VideoState
import com.musichoarder.app.ui.theme.MhTheme

/**
 * What the middle of the player screen is showing. The labels are the tab strip's, and `Song` sits
 * where the web's panel puts `Metadata` — the phone's home for a track is its artwork, not the
 * enrichment record.
 */
private enum class PlayerPane(val label: String) {
    Song("Song"),
    Lyrics("Lyrics"),
    Video("Video"),
}

/**
 * The full-screen player, built to read as the same thing as the web panel it mirrors: a pill tab
 * strip between a close button and the heart, a big cover over an ambient wash of itself, the
 * `artist · album` line, and one hairline-scrubber transport.
 *
 * The music video is a *backdrop* by default, exactly as on the web: muted, behind everything,
 * slaved to the audio clock. The Video tab promotes it to a watch view where it is letterboxed
 * rather than cropped and the scrim lifts.
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
    onToggleLike: () -> Unit,
    onCollapse: () -> Unit,
    onPlayPause: () -> Unit,
    onNext: () -> Unit,
    onPrevious: () -> Unit,
    onSeek: (Long) -> Unit,
    onSetSpeed: (Float) -> Unit,
    onToggleShuffle: () -> Unit,
    onCycleRepeat: () -> Unit,
    onAttachVideoSurface: (SurfaceView) -> Unit,
    onDetachVideoSurface: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val colors = MhTheme.colors

    // Two different questions: is there still a clip to offer a tab for, and is one running right
    // now? Switching the backdrop off stops the clip, and must not take the Video tab with it.
    val watchable = videoState.isWatchable
    var pane by rememberSaveable { mutableStateOf(PlayerPane.Song) }
    var lyricsExpanded by rememberSaveable { mutableStateOf(false) }
    // The track whose default tab has already been decided (or overridden by a tap).
    var settledTrackId by rememberSaveable { mutableStateOf<Int?>(null) }

    // A song without a video must not strand the screen in a watch view it can no longer show.
    if (pane == PlayerPane.Video && !watchable) pane = PlayerPane.Song

    // The web opens the panel on Lyrics when the track has any, else on the first tab — and only
    // ever on a song *change*, so a manual switch is never clobbered. Lyrics arrive asynchronously
    // here, so the decision waits for the fetch to answer and a tap settles the track early.
    LaunchedEffect(state.trackId, lyricsState) {
        val id = state.trackId ?: return@LaunchedEffect
        if (id == settledTrackId || lyricsState is LyricsUiState.Loading) return@LaunchedEffect
        settledTrackId = id
        pane = if (lyricsState.hasLyrics) PlayerPane.Lyrics else PlayerPane.Song
    }

    val panes = remember(watchable) {
        if (watchable) listOf(PlayerPane.Song, PlayerPane.Lyrics, PlayerPane.Video)
        else listOf(PlayerPane.Song, PlayerPane.Lyrics)
    }
    val watching = pane == PlayerPane.Video
    // Anything painted over a running clip needs the web's text-shadow treatment to stay readable.
    val onSurface = videoState.isVisible && showVideoBackdrop

    Box(modifier = modifier.fillMaxSize().background(colors.background)) {
        PlayerBackdrop(
            ambientCoverUrl = ambientCoverUrl,
            state = state,
            videoState = videoState,
            showVideo = videoState.isVisible && showVideoBackdrop,
            watching = watching,
            onAttachVideoSurface = onAttachVideoSurface,
            onDetachVideoSurface = onDetachVideoSurface,
        )

        Column(modifier = Modifier.fillMaxSize()) {
            PlayerTopBar(
                panes = panes,
                selected = pane,
                isLiked = isLiked,
                onSelect = {
                    settledTrackId = state.trackId
                    // You cannot watch the video with the video switched off.
                    if (it == PlayerPane.Video && !showVideoBackdrop) onToggleVideoBackdrop()
                    pane = it
                },
                onClose = onCollapse,
                onToggleLike = onToggleLike,
            )

            Box(modifier = Modifier.weight(1f).fillMaxWidth()) {
                Crossfade(targetState = pane, animationSpec = tween(220), label = "player-pane") { current ->
                    when (current) {
                        PlayerPane.Song -> SongPane(state, coverUrl, onSurface)

                        PlayerPane.Lyrics -> LyricsPane(
                            state = state,
                            coverUrl = coverUrl,
                            lyricsState = lyricsState,
                            onSurface = onSurface,
                            onPlayPause = onPlayPause,
                            onNext = onNext,
                            onPrevious = onPrevious,
                            onSeek = onSeek,
                            onSetSpeed = onSetSpeed,
                            onExpandLyrics = { lyricsExpanded = true },
                        )

                        // The clip is already painted full-bleed behind this column; the watch pane
                        // just names the track and gets out of its way.
                        PlayerPane.Video -> VideoPane(state, coverUrl, onSurface = true)
                    }
                }
            }

            // The Lyrics pane carries its own transport in the hero, the way the web's does.
            if (pane != PlayerPane.Lyrics) {
                PlayerTransport(
                    state = state,
                    onPlayPause = onPlayPause,
                    onNext = onNext,
                    onPrevious = onPrevious,
                    onSeek = onSeek,
                    onSetSpeed = onSetSpeed,
                    onSurface = onSurface || watching,
                    modifier = Modifier.padding(horizontal = 20.dp),
                )
            }

            PlayerBottomChrome(
                state = state,
                // On the watch pane the clip is the point, so there is nothing to switch off there.
                hasVideo = watchable && !watching,
                showVideoBackdrop = showVideoBackdrop,
                onToggleShuffle = onToggleShuffle,
                onCycleRepeat = onCycleRepeat,
                onToggleVideoBackdrop = onToggleVideoBackdrop,
            )

            state.error?.let {
                Text(
                    it,
                    style = MaterialTheme.typography.bodySmall,
                    color = colors.destructive,
                    textAlign = TextAlign.Center,
                    modifier = Modifier.fillMaxWidth().padding(horizontal = 20.dp, vertical = 4.dp),
                )
            }
        }
    }

    if (lyricsExpanded) {
        LyricsFullscreen(
            state = lyricsState,
            playerState = state,
            coverUrl = coverUrl,
            ambientUrl = ambientCoverUrl,
            onPlayPause = onPlayPause,
            onSeek = onSeek,
            onSetSpeed = onSetSpeed,
            onClose = { lyricsExpanded = false },
        )
    }
}

/**
 * The layers under the chrome: the cover blurred to a wash, then the clip, then whatever scrim the
 * current pane needs to keep text on top of it readable.
 */
@Composable
private fun PlayerBackdrop(
    ambientCoverUrl: String?,
    state: PlayerUiState,
    videoState: VideoState,
    showVideo: Boolean,
    watching: Boolean,
    onAttachVideoSurface: (SurfaceView) -> Unit,
    onDetachVideoSurface: () -> Unit,
) {
    val colors = MhTheme.colors

    AmbientBackdrop(
        url = ambientCoverUrl,
        artist = state.artist,
        title = state.album.ifBlank { state.title },
        // Watching wants a dark room, not a tinted page — the letterbox should read as a frame.
        scrimAlpha = if (watching) 0.92f else 0.8f,
        modifier = Modifier.fillMaxSize(),
    )
    if (watching) {
        Box(Modifier.fillMaxSize().background(Color.Black.copy(alpha = 0.55f)))
    }

    if (!showVideo) return

    // The surface stays mounted whenever a clip is running, so the decoder is not torn down every
    // time the pane changes and the first frame is already there when it is promoted. Only the fit
    // changes: cropped to fill behind the player, letterboxed when you are actually watching.
    PlayerVideoLayer(
        aspectRatio = videoState.aspectRatio,
        crop = !watching,
        onAttach = onAttachVideoSurface,
        onDetach = onDetachVideoSurface,
        modifier = Modifier.fillMaxSize(),
    )

    if (watching) {
        // The picture stays undimmed; the chrome gets its own gradients to sit on instead.
        Box(
            modifier = Modifier.fillMaxSize().background(
                Brush.verticalGradient(
                    0f to Color.Black.copy(alpha = 0.7f),
                    0.22f to Color.Transparent,
                    0.62f to Color.Transparent,
                    1f to Color.Black.copy(alpha = 0.8f),
                )
            )
        )
    } else {
        // As a backdrop the clip is atmosphere and legibility wins, so it sits under the same
        // vertical wash the web uses — heaviest at the top and foot, where the chrome lives.
        Box(
            modifier = Modifier.fillMaxSize().background(
                Brush.verticalGradient(
                    0f to colors.background.copy(alpha = 0.75f),
                    0.5f to colors.background.copy(alpha = 0.45f),
                    1f to colors.background.copy(alpha = 0.85f),
                )
            )
        )
    }
}

/** Close · the segmented tab strip · the heart. The web's share button is not implemented here. */
@Composable
private fun PlayerTopBar(
    panes: List<PlayerPane>,
    selected: PlayerPane,
    isLiked: Boolean,
    onSelect: (PlayerPane) -> Unit,
    onClose: () -> Unit,
    onToggleLike: () -> Unit,
) {
    val colors = MhTheme.colors
    Row(
        modifier = Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        MhCircleIconButton(
            icon = Icons.Rounded.Close,
            contentDescription = "Close player",
            onClick = onClose,
        )
        MhPillTabs(
            labels = panes.map { it.label },
            selectedIndex = panes.indexOf(selected).coerceAtLeast(0),
            onSelect = { onSelect(panes[it]) },
            modifier = Modifier.weight(1f),
        )
        MhCircleIconButton(
            icon = if (isLiked) Icons.Rounded.Favorite else Icons.Rounded.FavoriteBorder,
            contentDescription = if (isLiked) "Remove from liked songs" else "Add to liked songs",
            onClick = onToggleLike,
            tint = if (isLiked) colors.primary else colors.foreground,
        )
    }
}

@Composable
private fun SongPane(state: PlayerUiState, coverUrl: String?, onSurface: Boolean) {
    Column(
        modifier = Modifier.fillMaxSize().padding(horizontal = 24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        HeroCover(state, coverUrl)
        Spacer(Modifier.height(20.dp))
        TrackTitle(state.title, onSurface, textAlign = TextAlign.Center)
        Spacer(Modifier.height(4.dp))
        TrackSubtitle(state.artist, state.album, onSurface, textAlign = TextAlign.Center)
    }
}

@Composable
private fun LyricsPane(
    state: PlayerUiState,
    coverUrl: String?,
    lyricsState: LyricsUiState,
    onSurface: Boolean,
    onPlayPause: () -> Unit,
    onNext: () -> Unit,
    onPrevious: () -> Unit,
    onSeek: (Long) -> Unit,
    onSetSpeed: (Float) -> Unit,
    onExpandLyrics: () -> Unit,
) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 20.dp)
            .padding(top = 16.dp, bottom = 24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        HeroCover(state, coverUrl)
        Spacer(Modifier.height(20.dp))
        TrackTitle(state.title, onSurface, textAlign = TextAlign.Center)
        Spacer(Modifier.height(4.dp))
        TrackSubtitle(state.artist, state.album, onSurface, textAlign = TextAlign.Center)
        Spacer(Modifier.height(24.dp))
        PlayerTransport(
            state = state,
            onPlayPause = onPlayPause,
            onNext = onNext,
            onPrevious = onPrevious,
            onSeek = onSeek,
            onSetSpeed = onSetSpeed,
            onSurface = onSurface,
            modifier = Modifier.widthIn(max = 340.dp),
        )
        Spacer(Modifier.height(32.dp))
        LyricsCard(
            state = lyricsState,
            positionMs = state.positionMs,
            onExpand = onExpandLyrics,
        )
    }
}

@Composable
private fun VideoPane(state: PlayerUiState, coverUrl: String?, onSurface: Boolean) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 20.dp)
            .padding(top = 4.dp, bottom = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Artwork(
            url = coverUrl,
            artist = state.artist,
            title = state.album.ifBlank { state.title },
            modifier = Modifier.size(56.dp),
            shape = RoundedCornerShape(8.dp),
        )
        Column(modifier = Modifier.weight(1f)) {
            Text(
                state.title,
                style = legible(CompactTitleStyle, onSurface),
                color = MhTheme.colors.foreground,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            TrackSubtitle(state.artist, state.album, onSurface, textAlign = TextAlign.Start, small = true)
        }
    }
}

@Composable
private fun HeroCover(state: PlayerUiState, coverUrl: String?) {
    Artwork(
        url = coverUrl,
        artist = state.artist,
        title = state.album.ifBlank { state.title },
        shape = HeroShape,
        modifier = Modifier
            .widthIn(max = 224.dp)
            .fillMaxWidth()
            .aspectRatio(1f)
            .shadow(24.dp, HeroShape, clip = false, ambientColor = Color.Black, spotColor = Color.Black),
    )
}

@Composable
private fun TrackTitle(title: String, onSurface: Boolean, textAlign: TextAlign) {
    Text(
        text = title,
        style = legible(HeroTitleStyle, onSurface),
        color = MhTheme.colors.foreground,
        // One line, like the web's `truncate`: a long "(feat. ...)" title would otherwise push the
        // cover around from track to track.
        maxLines = 1,
        overflow = TextOverflow.Ellipsis,
        textAlign = textAlign,
    )
}

/** `artist · album`, with the album half a shade quieter — the web's `text-muted-foreground/70`. */
@Composable
private fun TrackSubtitle(
    artist: String,
    album: String,
    onSurface: Boolean,
    textAlign: TextAlign,
    small: Boolean = false,
) {
    val colors = MhTheme.colors
    val text = buildAnnotatedString {
        withStyle(SpanStyle(color = colors.mutedForeground)) { append(artist) }
        if (album.isNotBlank()) {
            withStyle(SpanStyle(color = colors.mutedForeground)) { append(" · ") }
            withStyle(SpanStyle(color = colors.mutedForeground.copy(alpha = 0.7f))) { append(album) }
        }
    }
    Text(
        text = text,
        style = legible(if (small) MaterialTheme.typography.bodySmall else SubtitleStyle, onSurface),
        maxLines = 1,
        overflow = TextOverflow.Ellipsis,
        textAlign = textAlign,
    )
}

/**
 * Shuffle and repeat, plus the backdrop toggle.
 *
 * The web transport has no queue modes at all — they live elsewhere in the app — but they work on
 * the phone today and should not regress, so they get a quiet row of their own rather than being
 * pushed back into the transport and knocking the play button off the centre line. The film glyph
 * is the web's `Film` button, reduced to the one control in its popover that is not an owner-only
 * mutation: whether the clip plays behind the player.
 */
@Composable
private fun PlayerBottomChrome(
    state: PlayerUiState,
    hasVideo: Boolean,
    showVideoBackdrop: Boolean,
    onToggleShuffle: () -> Unit,
    onCycleRepeat: () -> Unit,
    onToggleVideoBackdrop: () -> Unit,
) {
    val colors = MhTheme.colors
    Row(
        modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        // Mirrors the film button so the queue glyphs stay on the centre line either way.
        Spacer(Modifier.width(36.dp))
        Row(
            modifier = Modifier.weight(1f),
            horizontalArrangement = Arrangement.Center,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            ChromeGlyph(
                icon = Icons.Rounded.Shuffle,
                contentDescription = "Shuffle",
                tint = if (state.shuffleEnabled) colors.primary else colors.mutedForeground,
                onClick = onToggleShuffle,
            )
            Spacer(Modifier.width(20.dp))
            ChromeGlyph(
                icon = if (state.repeatMode == Player.REPEAT_MODE_ONE) Icons.Rounded.RepeatOne
                else Icons.Rounded.Repeat,
                contentDescription = "Repeat",
                tint = if (state.repeatMode == Player.REPEAT_MODE_OFF) colors.mutedForeground
                else colors.primary,
                onClick = onCycleRepeat,
            )
        }
        if (hasVideo) {
            ChromeGlyph(
                icon = Icons.Rounded.LocalMovies,
                contentDescription = if (showVideoBackdrop) "Hide the music video" else "Show the music video",
                tint = if (showVideoBackdrop) colors.primary else colors.mutedForeground,
                onClick = onToggleVideoBackdrop,
                ground = true,
            )
        } else {
            Spacer(Modifier.width(36.dp))
        }
    }
}

@Composable
private fun ChromeGlyph(
    icon: ImageVector,
    contentDescription: String,
    tint: Color,
    onClick: () -> Unit,
    ground: Boolean = false,
) {
    val colors = MhTheme.colors
    Box(
        modifier = Modifier
            .size(36.dp)
            .clip(CircleShape)
            .background(if (ground) colors.background.copy(alpha = 0.4f) else Color.Transparent)
            .clickable(onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        Icon(icon, contentDescription = contentDescription, tint = tint, modifier = Modifier.size(18.dp))
    }
}

private val HeroShape = RoundedCornerShape(12.dp)

/** `text-2xl font-bold tracking-[-0.02em]` — the panel's hero title. */
private val HeroTitleStyle = TextStyle(
    fontFamily = FontFamily.Default,
    fontSize = 24.sp,
    lineHeight = 32.sp,
    fontWeight = FontWeight.Bold,
    letterSpacing = (-0.48).sp,
)

/** `text-base font-semibold tracking-[-0.01em]` — the compact header's title. */
private val CompactTitleStyle = TextStyle(
    fontFamily = FontFamily.Default,
    fontSize = 16.sp,
    lineHeight = 20.sp,
    fontWeight = FontWeight.SemiBold,
    letterSpacing = (-0.16).sp,
)

/** `text-sm` — the `artist · album` line under a hero title. */
private val SubtitleStyle = TextStyle(
    fontFamily = FontFamily.Default,
    fontSize = 14.sp,
    lineHeight = 20.sp,
)
