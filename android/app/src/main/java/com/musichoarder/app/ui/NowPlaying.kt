package com.musichoarder.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectVerticalDragGestures
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
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
import androidx.compose.runtime.getValue
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.musichoarder.app.player.PlayerUiState
import com.musichoarder.app.ui.theme.MhTheme

/**
 * The bar that follows you around the app — the web's compact `MiniPlayer`: a 56dp capsule docked
 * just above the navigation bar, reading `[art] title / artist  ⏯ ⏭` so the controls sit on the
 * thumb's side and the song is read first, the way Apple Music and every Android player order it.
 *
 * No progress line, as on the web's compact bar: the capsule is for knowing what is playing and
 * pausing it, and the player is one tap (or an upward swipe) away for anything finer.
 *
 * Opaque [MhColors.chromeSolid] rather than the web's blurred glass — Compose has no cheap
 * backdrop blur, and the solid chrome is exactly what the web itself falls back to under Reduce
 * Transparency. The half-pixel rim and the soft shadow are what lift it off the page.
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
    val expand by rememberUpdatedState(onExpand)
    Row(
        modifier = modifier
            .fillMaxWidth()
            .padding(horizontal = 8.dp)
            .shadow(
                elevation = 10.dp,
                shape = CircleShape,
                ambientColor = Color.Black.copy(alpha = 0.12f),
                spotColor = Color.Black.copy(alpha = 0.12f),
            )
            .clip(CircleShape)
            .background(colors.chromeSolid)
            .border(0.5.dp, colors.chromeRim, CircleShape)
            // A floor, not a height, so a large font scale grows the capsule instead of clipping.
            .heightIn(min = 56.dp)
            .clickable(onClickLabel = "Open player", onClick = onExpand)
            // Swipe up opens the player too — the web capsule's gesture. The tap above stays the
            // accessible path; a drag only acts once it has clearly gone upwards.
            .pointerInput(Unit) {
                val threshold = SWIPE_UP_DISTANCE.toPx()
                var travelled = 0f
                detectVerticalDragGestures(
                    onDragStart = { travelled = 0f },
                    onVerticalDrag = { change, dragAmount ->
                        travelled += dragAmount
                        if (travelled < -threshold) {
                            change.consume()
                            travelled = 0f
                            expand()
                        }
                    },
                )
            }
            .padding(start = 8.dp, end = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Artwork(
            url = coverUrl,
            artist = state.artist,
            title = state.album.ifBlank { state.title },
            modifier = Modifier.size(40.dp),
            shape = RoundedCornerShape(6.dp),
        )
        Spacer(Modifier.size(12.dp))
        Column(modifier = Modifier.weight(1f).padding(vertical = 8.dp)) {
            Text(
                state.title,
                style = MaterialTheme.typography.bodyMedium,
                fontWeight = FontWeight.SemiBold,
                color = colors.foreground,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            Text(
                state.artist,
                style = MaterialTheme.typography.bodySmall,
                color = colors.mutedForeground,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
        // Full 48dp Material targets, laid out at that size rather than squeezed to the glyph: the
        // text column ends where the buttons begin, so a tap meant to open the player can never
        // land on Play and a tap on Play can never open the player.
        val buffering = state.isBuffering && !state.isPlaying
        IconButton(
            onClick = onPlayPause,
            // The spinner has no words of its own, so TalkBack would find an unlabelled button.
            // "Loading", not "Pause": a tap here asks it to play, which it is already trying to do.
            modifier = if (buffering) Modifier.semantics { contentDescription = "Loading" } else Modifier,
        ) {
            if (buffering) {
                CircularProgressIndicator(
                    modifier = Modifier.size(20.dp),
                    strokeWidth = 2.dp,
                    color = colors.foreground,
                )
            } else {
                Icon(
                    if (state.isPlaying) Icons.Rounded.Pause else Icons.Rounded.PlayArrow,
                    contentDescription = if (state.isPlaying) "Pause" else "Play",
                    tint = colors.foreground,
                    modifier = Modifier.size(28.dp),
                )
            }
        }
        IconButton(onClick = onNext, enabled = state.hasNext) {
            Icon(
                Icons.Rounded.SkipNext,
                contentDescription = "Next track",
                tint = if (state.hasNext) colors.foreground else colors.mutedForegroundDim,
                modifier = Modifier.size(26.dp),
            )
        }
    }
}

/** How far a drag has to travel upwards before it counts as "open the player". */
private val SWIPE_UP_DISTANCE = 24.dp
