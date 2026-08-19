package com.musichoarder.app.ui

import android.view.SurfaceView
import android.view.ViewGroup
import androidx.compose.animation.Crossfade
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
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.KeyboardArrowDown
import androidx.compose.material.icons.rounded.Lyrics
import androidx.compose.material.icons.rounded.Pause
import androidx.compose.material.icons.rounded.PlayArrow
import androidx.compose.material.icons.rounded.Repeat
import androidx.compose.material.icons.rounded.RepeatOne
import androidx.compose.material.icons.rounded.Shuffle
import androidx.compose.material.icons.rounded.SkipNext
import androidx.compose.material.icons.rounded.SkipPrevious
import androidx.compose.material.icons.rounded.Videocam
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.media3.common.Player
import com.musichoarder.app.player.PlayerUiState
import com.musichoarder.app.player.VideoState
import com.musichoarder.app.ui.theme.MhTheme

/** What the middle of the player screen is showing. */
private enum class PlayerPane { Artwork, Lyrics, Video }

/**
 * The full-screen player: big art, a real scrubber, the transport controls, and the two extras the
 * web player has — synced lyrics and the song's music video.
 *
 * The video is a *backdrop* by default, exactly as on the web: muted, behind everything, slaved to
 * the audio clock. Tapping the video button promotes it to a watch view where it replaces the
 * artwork and the scrim lifts.
 */
@Composable
fun NowPlayingScreen(
    state: PlayerUiState,
    coverUrl: String?,
    lyricsState: LyricsUiState,
    videoState: VideoState,
    onCollapse: () -> Unit,
    onPlayPause: () -> Unit,
    onNext: () -> Unit,
    onPrevious: () -> Unit,
    onSeek: (Long) -> Unit,
    onToggleShuffle: () -> Unit,
    onCycleRepeat: () -> Unit,
    onAttachVideoSurface: (SurfaceView) -> Unit,
    onDetachVideoSurface: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val colors = MhTheme.colors
    var pane by remember { mutableStateOf(PlayerPane.Artwork) }

    // A song without a video must not strand the screen in a watch view it can no longer show.
    if (pane == PlayerPane.Video && !(videoState.hasVideo && videoState.isVisible)) {
        pane = PlayerPane.Artwork
    }

    // While a finger is on the slider the player's own position must not fight the drag.
    var scrubPosition by remember { mutableFloatStateOf(0f) }
    var isScrubbing by remember { mutableStateOf(false) }
    val position = if (isScrubbing) scrubPosition else state.positionMs.toFloat()
    // Duration is unknown for the first moments of a track (and for some containers, longer). Left
    // to itself the slider would clamp the position into a 0..0 range and sit pinned at 100%, which
    // reads as a bug; show an inert bar and "--:--" until the real length arrives.
    val hasDuration = state.durationMs > 0
    val duration = state.durationMs.toFloat().coerceAtLeast(1f)

    Box(modifier = modifier.fillMaxSize().background(colors.background)) {
        // The surface stays mounted whenever a clip exists, so the decoder is not torn down every
        // time the pane changes and the first frame is already there when it is promoted.
        // `isVisible` — not just `hasVideo`: once the song outlives the clip, or both stream retries
        // fail, the controller drops it and the surface has to come down with it. Keying on
        // `hasVideo` alone left a frozen last frame sitting behind the scrim.
        if (videoState.hasVideo && videoState.isVisible) {
            VideoSurface(
                onAttach = onAttachVideoSurface,
                onDetach = onDetachVideoSurface,
                modifier = Modifier.fillMaxSize(),
            )
            // As a backdrop the clip is atmosphere and legibility wins, so it sits under a heavy
            // scrim in the page colour — heavier in the light theme, where a saturated frame
            // bleeds straight through pale text. The watch pane scrims with black instead: a
            // video wants to look like a video, not like a tinted page.
            Box(
                modifier = Modifier
                    .fillMaxSize()
                    .background(
                        if (pane == PlayerPane.Video) Color.Black.copy(alpha = 0.25f)
                        else colors.background.copy(alpha = if (colors.isDark) 0.86f else 0.95f)
                    )
            )
            // Watching means the flat scrim stays light enough to see the clip, which leaves the
            // transport sitting on whatever the video happens to be showing. A gradient foot keeps
            // the controls readable over a bright frame without dimming the picture itself.
            if (pane == PlayerPane.Video) {
                Box(
                    modifier = Modifier
                        .fillMaxSize()
                        .background(
                            Brush.verticalGradient(
                                0.55f to Color.Transparent,
                                1f to Color.Black.copy(alpha = 0.75f),
                            )
                        )
                )
            }
        }

        Column(
            modifier = Modifier.fillMaxSize().padding(horizontal = 24.dp, vertical = 12.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                IconButton(onClick = onCollapse) {
                    Icon(
                        Icons.Rounded.KeyboardArrowDown,
                        contentDescription = "Collapse player",
                        tint = colors.mutedForeground,
                    )
                }
                Text(
                    text = state.album,
                    style = MaterialTheme.typography.labelMedium,
                    color = colors.mutedForeground,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    textAlign = TextAlign.Center,
                    modifier = Modifier.weight(1f),
                )
                Spacer(Modifier.size(48.dp))
            }

            Box(
                modifier = Modifier.weight(1f).fillMaxWidth(),
                contentAlignment = Alignment.Center,
            ) {
                Crossfade(targetState = pane, label = "player-pane") { current ->
                    when (current) {
                        PlayerPane.Artwork -> Box(
                            modifier = Modifier.fillMaxSize(),
                            contentAlignment = Alignment.Center,
                        ) {
                            Artwork(
                                url = coverUrl,
                                artist = state.artist,
                                title = state.album.ifBlank { state.title },
                                modifier = Modifier.widthIn(max = 420.dp).fillMaxWidth().aspectRatio(1f),
                                shape = RoundedCornerShape(12.dp),
                            )
                        }

                        PlayerPane.Lyrics -> LyricsView(
                            state = lyricsState,
                            positionMs = state.positionMs,
                            onSeek = onSeek,
                        )

                        // The clip itself is already painted full-bleed behind this column; the
                        // watch pane just gets out of its way.
                        PlayerPane.Video -> Box(Modifier.fillMaxSize())
                    }
                }
            }

            Spacer(Modifier.height(20.dp))

            Text(
                state.title,
                style = MaterialTheme.typography.headlineSmall,
                color = colors.foreground,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
                textAlign = TextAlign.Center,
            )
            Spacer(Modifier.height(6.dp))
            Text(
                state.artist,
                style = MaterialTheme.typography.bodyLarge,
                color = colors.mutedForeground,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )

            Spacer(Modifier.height(16.dp))

            Slider(
                value = if (hasDuration) position.coerceIn(0f, duration) else 0f,
                valueRange = 0f..duration,
                enabled = hasDuration,
                onValueChange = {
                    isScrubbing = true
                    scrubPosition = it
                },
                onValueChangeFinished = {
                    onSeek(scrubPosition.toLong())
                    isScrubbing = false
                },
                colors = SliderDefaults.colors(
                    thumbColor = colors.foreground,
                    activeTrackColor = colors.foreground,
                    inactiveTrackColor = colors.muted,
                    disabledThumbColor = colors.mutedForeground,
                    disabledActiveTrackColor = colors.muted,
                    disabledInactiveTrackColor = colors.muted,
                ),
                modifier = Modifier.fillMaxWidth(),
            )
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
            ) {
                Text(
                    formatDuration(position.toLong()),
                    style = MaterialTheme.typography.labelSmall,
                    fontFamily = FontFamily.Monospace,
                    color = colors.mutedForeground,
                )
                Text(
                    if (hasDuration) formatDuration(state.durationMs) else "--:--",
                    style = MaterialTheme.typography.labelSmall,
                    fontFamily = FontFamily.Monospace,
                    color = colors.mutedForeground,
                )
            }

            Spacer(Modifier.height(12.dp))

            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceEvenly,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                IconButton(onClick = onToggleShuffle) {
                    Icon(
                        Icons.Rounded.Shuffle,
                        contentDescription = "Shuffle",
                        tint = if (state.shuffleEnabled) colors.primary else colors.mutedForeground,
                    )
                }
                IconButton(onClick = onPrevious, enabled = state.hasPrevious) {
                    Icon(
                        Icons.Rounded.SkipPrevious,
                        contentDescription = "Previous track",
                        tint = if (state.hasPrevious) colors.foreground else colors.mutedForeground,
                        modifier = Modifier.size(34.dp),
                    )
                }
                // The one filled control on the screen, in the brand green.
                Box(
                    modifier = Modifier
                        .size(64.dp)
                        .clip(CircleShape)
                        .background(colors.primary)
                        .clickable(onClick = onPlayPause),
                    contentAlignment = Alignment.Center,
                ) {
                    if (state.isBuffering && !state.isPlaying) {
                        CircularProgressIndicator(
                            modifier = Modifier.size(24.dp),
                            strokeWidth = 3.dp,
                            color = colors.primaryForeground,
                        )
                    } else {
                        Icon(
                            if (state.isPlaying) Icons.Rounded.Pause else Icons.Rounded.PlayArrow,
                            contentDescription = if (state.isPlaying) "Pause" else "Play",
                            tint = colors.primaryForeground,
                            modifier = Modifier.size(32.dp),
                        )
                    }
                }
                IconButton(onClick = onNext, enabled = state.hasNext) {
                    Icon(
                        Icons.Rounded.SkipNext,
                        contentDescription = "Next track",
                        tint = if (state.hasNext) colors.foreground else colors.mutedForeground,
                        modifier = Modifier.size(34.dp),
                    )
                }
                IconButton(onClick = onCycleRepeat) {
                    Icon(
                        if (state.repeatMode == Player.REPEAT_MODE_ONE) Icons.Rounded.RepeatOne
                        else Icons.Rounded.Repeat,
                        contentDescription = "Repeat",
                        tint = if (state.repeatMode == Player.REPEAT_MODE_OFF) colors.mutedForeground
                        else colors.primary,
                    )
                }
            }

            Spacer(Modifier.height(10.dp))

            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                PaneToggle(
                    icon = Icons.Rounded.Lyrics,
                    label = "Lyrics",
                    selected = pane == PlayerPane.Lyrics,
                ) {
                    pane = if (pane == PlayerPane.Lyrics) PlayerPane.Artwork else PlayerPane.Lyrics
                }
                if (videoState.hasVideo && videoState.isVisible) {
                    PaneToggle(
                        icon = Icons.Rounded.Videocam,
                        label = "Video",
                        selected = pane == PlayerPane.Video,
                    ) {
                        pane = if (pane == PlayerPane.Video) PlayerPane.Artwork else PlayerPane.Video
                    }
                }
            }

            state.error?.let {
                Spacer(Modifier.height(8.dp))
                Text(
                    it,
                    style = MaterialTheme.typography.bodySmall,
                    color = colors.destructive,
                    textAlign = TextAlign.Center,
                    modifier = Modifier.fillMaxWidth(),
                )
            }
        }
    }
}

/** The pill toggles under the transport, in the shell's chip idiom. */
@Composable
private fun PaneToggle(
    icon: ImageVector,
    label: String,
    selected: Boolean,
    onClick: () -> Unit,
) {
    val colors = MhTheme.colors
    Row(
        modifier = Modifier
            .clip(CircleShape)
            .background(if (selected) colors.primary.copy(alpha = 0.16f) else colors.secondary)
            .clickable(onClick = onClick)
            .padding(horizontal = 14.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(
            icon,
            contentDescription = null,
            tint = if (selected) colors.primary else colors.mutedForeground,
            modifier = Modifier.size(15.dp),
        )
        Spacer(Modifier.size(6.dp))
        Text(
            label,
            style = MaterialTheme.typography.labelMedium,
            fontWeight = FontWeight.SemiBold,
            color = if (selected) colors.primary else colors.mutedForeground,
        )
    }
}

/**
 * The video output. ExoPlayer renders straight into a [SurfaceView] — media3-ui's PlayerView would
 * bring its own controls and layout, and all this needs is the pixels.
 */
@Composable
private fun VideoSurface(
    onAttach: (SurfaceView) -> Unit,
    onDetach: () -> Unit,
    modifier: Modifier = Modifier,
) {
    AndroidView(
        modifier = modifier,
        factory = { context ->
            SurfaceView(context).apply {
                layoutParams = ViewGroup.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT,
                    ViewGroup.LayoutParams.MATCH_PARENT,
                )
                onAttach(this)
            }
        },
    )
    DisposableEffect(Unit) { onDispose { onDetach() } }
}
