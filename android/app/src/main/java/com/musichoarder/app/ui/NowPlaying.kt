package com.musichoarder.app.ui

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
import androidx.compose.material3.FilledIconButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Slider
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.media3.common.Player
import com.musichoarder.app.player.PlayerUiState

/**
 * The bar that follows you around the app. Tapping it opens the full player; the thin progress line
 * along the bottom is the only progress indicator you need while browsing.
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
    Surface(
        modifier = modifier.fillMaxWidth(),
        color = MaterialTheme.colorScheme.surfaceContainerHigh,
        tonalElevation = 3.dp,
    ) {
        Column {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .clickable(onClick = onExpand)
                    .padding(horizontal = 10.dp, vertical = 8.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Artwork(
                    url = coverUrl,
                    seed = state.album.ifBlank { state.title },
                    modifier = Modifier.size(44.dp),
                    shape = RoundedCornerShape(6.dp),
                    letterSize = 16.sp,
                )
                Spacer(Modifier.size(12.dp))
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        state.title,
                        style = MaterialTheme.typography.bodyMedium,
                        fontWeight = FontWeight.Medium,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                    Text(
                        state.artist,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
                IconButton(onClick = onPlayPause) {
                    if (state.isBuffering && !state.isPlaying) {
                        CircularProgressIndicator(modifier = Modifier.size(20.dp), strokeWidth = 2.dp)
                    } else {
                        Icon(
                            if (state.isPlaying) Icons.Rounded.Pause else Icons.Rounded.PlayArrow,
                            contentDescription = if (state.isPlaying) "Pause" else "Play",
                        )
                    }
                }
                IconButton(onClick = onNext, enabled = state.hasNext) {
                    Icon(Icons.Rounded.SkipNext, contentDescription = "Next track")
                }
            }

            LinearProgressIndicator(
                progress = { state.progressFraction() },
                modifier = Modifier.fillMaxWidth().height(2.dp),
                trackColor = Color.Transparent,
                gapSize = 0.dp,
                drawStopIndicator = {},
            )
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
    // While a finger is on the slider the player's own position must not fight the drag.
    var scrubPosition by remember { mutableFloatStateOf(0f) }
    var isScrubbing by remember { mutableStateOf(false) }
    val position = if (isScrubbing) scrubPosition else state.positionMs.toFloat()
    // Duration is unknown for the first moments of a track (and for some containers, longer). Left
    // to itself the slider would clamp the position into a 0..0 range and sit pinned at 100%, which
    // reads as a bug; show an inert bar and "--:--" until the real length arrives.
    val hasDuration = state.durationMs > 0
    val duration = state.durationMs.toFloat().coerceAtLeast(1f)

    Surface(modifier = modifier.fillMaxSize(), color = MaterialTheme.colorScheme.surface) {
        Column(
            modifier = Modifier.fillMaxSize().padding(horizontal = 24.dp, vertical = 12.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                IconButton(onClick = onCollapse) {
                    Icon(Icons.Rounded.KeyboardArrowDown, contentDescription = "Collapse player")
                }
                Text(
                    text = state.album,
                    style = MaterialTheme.typography.labelLarge,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
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
                seed = state.album.ifBlank { state.title },
                modifier = Modifier.widthIn(max = 420.dp).fillMaxWidth().aspectRatio(1f),
                shape = RoundedCornerShape(16.dp),
                letterSize = 96.sp,
            )

            Spacer(Modifier.height(32.dp))

            Text(
                state.title,
                style = MaterialTheme.typography.headlineSmall,
                fontWeight = FontWeight.SemiBold,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
                textAlign = TextAlign.Center,
            )
            Spacer(Modifier.height(6.dp))
            Text(
                state.artist,
                style = MaterialTheme.typography.bodyLarge,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )

            Spacer(Modifier.height(24.dp))

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
                modifier = Modifier.fillMaxWidth(),
            )
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
            ) {
                Text(
                    formatDuration(position.toLong()),
                    style = MaterialTheme.typography.labelMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                Text(
                    if (hasDuration) formatDuration(state.durationMs) else "--:--",
                    style = MaterialTheme.typography.labelMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
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
                        tint = if (state.shuffleEnabled) MaterialTheme.colorScheme.primary
                        else MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                IconButton(onClick = onPrevious, enabled = state.hasPrevious) {
                    Icon(
                        Icons.Rounded.SkipPrevious,
                        contentDescription = "Previous track",
                        modifier = Modifier.size(36.dp),
                    )
                }
                FilledIconButton(onClick = onPlayPause, modifier = Modifier.size(68.dp)) {
                    if (state.isBuffering && !state.isPlaying) {
                        CircularProgressIndicator(modifier = Modifier.size(26.dp), strokeWidth = 3.dp)
                    } else {
                        Icon(
                            if (state.isPlaying) Icons.Rounded.Pause else Icons.Rounded.PlayArrow,
                            contentDescription = if (state.isPlaying) "Pause" else "Play",
                            modifier = Modifier.size(34.dp),
                        )
                    }
                }
                IconButton(onClick = onNext, enabled = state.hasNext) {
                    Icon(
                        Icons.Rounded.SkipNext,
                        contentDescription = "Next track",
                        modifier = Modifier.size(36.dp),
                    )
                }
                IconButton(onClick = onCycleRepeat) {
                    Icon(
                        if (state.repeatMode == Player.REPEAT_MODE_ONE) Icons.Rounded.RepeatOne
                        else Icons.Rounded.Repeat,
                        contentDescription = "Repeat",
                        tint = if (state.repeatMode == Player.REPEAT_MODE_OFF)
                            MaterialTheme.colorScheme.onSurfaceVariant
                        else MaterialTheme.colorScheme.primary,
                    )
                }
            }

            Spacer(Modifier.weight(1f))

            state.error?.let {
                Box(modifier = Modifier.fillMaxWidth(), contentAlignment = Alignment.Center) {
                    Text(
                        it,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.error,
                        textAlign = TextAlign.Center,
                    )
                }
            }
        }
    }
}

private fun PlayerUiState.progressFraction(): Float =
    if (durationMs <= 0) 0f else (positionMs.toFloat() / durationMs.toFloat()).coerceIn(0f, 1f)
