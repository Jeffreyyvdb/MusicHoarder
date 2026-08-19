package com.musichoarder.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
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
import androidx.compose.material.icons.rounded.Pause
import androidx.compose.material.icons.rounded.PlayArrow
import androidx.compose.material.icons.rounded.Repeat
import androidx.compose.material.icons.rounded.RepeatOne
import androidx.compose.material.icons.rounded.Shuffle
import androidx.compose.material.icons.rounded.SkipNext
import androidx.compose.material.icons.rounded.SkipPrevious
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.media3.common.Player
import com.musichoarder.app.player.PlayerUiState
import com.musichoarder.app.ui.theme.MhFloatingShape
import com.musichoarder.app.ui.theme.MhTheme

/**
 * The floating bar that follows you around the app — the web's MiniPlayer: inset from both edges,
 * `rounded-2xl`, hairline border, with a thin progress line across the top and the transport on the
 * left of the metadata.
 */
@Composable
fun MiniPlayer(
    state: PlayerUiState,
    coverUrl: String?,
    onExpand: () -> Unit,
    onPlayPause: () -> Unit,
    onNext: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val colors = MhTheme.colors
    Column(
        modifier = modifier
            .fillMaxWidth()
            .padding(horizontal = 12.dp)
            .clip(MhFloatingShape)
            .background(colors.card)
            .border(1.dp, colors.border, MhFloatingShape),
    ) {
        // `h-0.5` progress hairline. The track is a translucent tint of the foreground, as on the
        // web — the `muted` token is too close to the card colour to read as a track at all.
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .height(2.dp)
                .background(colors.foreground.copy(alpha = 0.15f))
        ) {
            Box(
                modifier = Modifier
                    .fillMaxWidth(state.progressFraction())
                    .height(2.dp)
                    .background(colors.primary)
            )
        }

        Row(
            modifier = Modifier
                .fillMaxWidth()
                .height(56.dp)
                .clickable(onClick = onExpand)
                .padding(horizontal = 10.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            IconButton(onClick = onPlayPause, modifier = Modifier.size(36.dp)) {
                if (state.isBuffering && !state.isPlaying) {
                    CircularProgressIndicator(
                        modifier = Modifier.size(18.dp),
                        strokeWidth = 2.dp,
                        color = colors.foreground,
                    )
                } else {
                    Icon(
                        if (state.isPlaying) Icons.Rounded.Pause else Icons.Rounded.PlayArrow,
                        contentDescription = if (state.isPlaying) "Pause" else "Play",
                        tint = colors.foreground,
                        modifier = Modifier.size(22.dp),
                    )
                }
            }
            IconButton(
                onClick = onNext,
                enabled = state.hasNext,
                modifier = Modifier.size(36.dp),
            ) {
                Icon(
                    Icons.Rounded.SkipNext,
                    contentDescription = "Next track",
                    tint = if (state.hasNext) colors.foreground else colors.mutedForeground,
                    modifier = Modifier.size(20.dp),
                )
            }

            Spacer(Modifier.size(8.dp))
            Artwork(
                url = coverUrl,
                artist = state.artist,
                title = state.album.ifBlank { state.title },
                modifier = Modifier.size(36.dp),
                shape = RoundedCornerShape(4.dp),
            )
            Spacer(Modifier.size(10.dp))
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    state.title,
                    style = MaterialTheme.typography.bodyMedium,
                    fontWeight = FontWeight.Medium,
                    color = colors.foreground,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                Text(
                    state.artist,
                    style = MaterialTheme.typography.labelSmall,
                    color = colors.mutedForeground,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        }
    }
}

/** The full-screen player: big art, a real scrubber, and the transport controls. */
@Composable
fun NowPlayingScreen(
    state: PlayerUiState,
    coverUrl: String?,
    onCollapse: () -> Unit,
    onPlayPause: () -> Unit,
    onNext: () -> Unit,
    onPrevious: () -> Unit,
    onSeek: (Long) -> Unit,
    onToggleShuffle: () -> Unit,
    onCycleRepeat: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val colors = MhTheme.colors

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

            Spacer(Modifier.weight(1f))

            Artwork(
                url = coverUrl,
                artist = state.artist,
                title = state.album.ifBlank { state.title },
                modifier = Modifier.widthIn(max = 420.dp).fillMaxWidth().aspectRatio(1f),
                shape = RoundedCornerShape(12.dp),
            )

            Spacer(Modifier.height(32.dp))

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

            Spacer(Modifier.height(20.dp))

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

            Spacer(Modifier.height(16.dp))

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

            Spacer(Modifier.weight(1f))

            state.error?.let {
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

private fun PlayerUiState.progressFraction(): Float =
    if (durationMs <= 0) 0f else (positionMs.toFloat() / durationMs.toFloat()).coerceIn(0f, 1f)
