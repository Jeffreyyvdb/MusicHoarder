package com.musichoarder.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Pause
import androidx.compose.material.icons.rounded.PlayArrow
import androidx.compose.material.icons.rounded.SkipNext
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
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

            // Material expands an IconButton's touch target to 48dp regardless of its 36dp visual
            // size, so it reaches 6dp past each edge. At the 8dp gap this had, Next's target
            // overlapped the cover and a tap meant to open the player skipped the track instead.
            Spacer(Modifier.size(16.dp))
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

private fun PlayerUiState.progressFraction(): Float =
    if (durationMs <= 0) 0f else (positionMs.toFloat() / durationMs.toFloat()).coerceIn(0f, 1f)
