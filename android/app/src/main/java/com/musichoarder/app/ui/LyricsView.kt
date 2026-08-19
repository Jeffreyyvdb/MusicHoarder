package com.musichoarder.app.ui

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.VerticalAlignCenter
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.musichoarder.app.data.Lyrics
import com.musichoarder.app.ui.theme.MhTheme

/** What the player screen knows about the current song's lyrics. */
sealed interface LyricsUiState {
    data object Loading : LyricsUiState
    data class Ready(val lyrics: Lyrics) : LyricsUiState
    data class Failed(val message: String) : LyricsUiState
}

/**
 * The synced lyrics viewer.
 *
 * Auto-scroll keeps the active line centred, but it must not fight the reader: touching the list
 * disengages following and a floating "Sync" pill re-engages it — the same contract the web panel
 * (and every other music player) uses. Disengaging keys off the touch itself rather than the scroll
 * position, because a scroll listener cannot tell our own animated scroll from a finger.
 */
@Composable
fun LyricsView(
    state: LyricsUiState,
    positionMs: Long,
    onSeek: (Long) -> Unit,
    modifier: Modifier = Modifier,
) {
    val colors = MhTheme.colors

    when (state) {
        is LyricsUiState.Loading -> LyricsMessage(modifier) { CircularProgressIndicator(color = colors.primary) }

        is LyricsUiState.Failed -> LyricsMessage(modifier) {
            Text(
                state.message,
                style = MaterialTheme.typography.bodyMedium,
                color = colors.mutedForeground,
                textAlign = TextAlign.Center,
            )
        }

        is LyricsUiState.Ready -> {
            val lyrics = state.lyrics
            when {
                lyrics.isInstrumental -> LyricsMessage(modifier) {
                    Text(
                        "Instrumental",
                        style = MaterialTheme.typography.bodyLarge,
                        color = colors.mutedForeground,
                    )
                }

                lyrics.isSynced -> SyncedLyrics(lyrics, positionMs, onSeek, modifier)

                !lyrics.plainText.isNullOrBlank() -> Column(
                    modifier = modifier
                        .fillMaxSize()
                        .verticalScroll(rememberScrollState())
                        .padding(horizontal = 8.dp, vertical = 24.dp),
                ) {
                    Text(
                        lyrics.plainText,
                        style = MaterialTheme.typography.bodyLarge,
                        color = colors.mutedForeground,
                        textAlign = TextAlign.Center,
                        modifier = Modifier.fillMaxWidth(),
                    )
                }

                else -> LyricsMessage(modifier) {
                    Text(
                        "No lyrics for this track.",
                        style = MaterialTheme.typography.bodyMedium,
                        color = colors.mutedForeground,
                    )
                }
            }
        }
    }
}

@Composable
private fun SyncedLyrics(
    lyrics: Lyrics,
    positionMs: Long,
    onSeek: (Long) -> Unit,
    modifier: Modifier,
) {
    val colors = MhTheme.colors
    val listState = rememberLazyListState()
    var followActive by remember(lyrics) { mutableStateOf(true) }

    // Last line whose timestamp has passed; -1 before the first.
    val activeIndex = remember(lyrics, positionMs) {
        var active = -1
        for (i in lyrics.lines.indices) {
            if (lyrics.lines[i].timeMs <= positionMs) active = i else break
        }
        active
    }

    BoxWithConstraints(modifier = modifier.fillMaxSize()) {
        // Centring the active line means offsetting by half the viewport; the list gets matching
        // padding so the first and last lines can reach the middle too.
        val halfViewport = maxHeight / 2

        LaunchedEffect(activeIndex, followActive) {
            if (!followActive || activeIndex < 0) return@LaunchedEffect
            listState.animateScrollToItem(activeIndex)
        }

        LazyColumn(
            state = listState,
            contentPadding = PaddingValues(top = halfViewport, bottom = halfViewport),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(14.dp),
            modifier = Modifier
                .fillMaxSize()
                // Watch the raw touch stream: any finger down on the list means the reader has taken
                // over. Initial pass so it fires before the scroll gesture consumes the event.
                .pointerInput(Unit) {
                    awaitEachGesture {
                        awaitFirstDown(requireUnconsumed = false, pass = PointerEventPass.Initial)
                        followActive = false
                    }
                },
        ) {
            itemsIndexed(lyrics.lines) { index, line ->
                val isActive = index == activeIndex
                Text(
                    text = line.text.ifBlank { "♪" },
                    style = MaterialTheme.typography.headlineSmall,
                    fontWeight = if (isActive) FontWeight.Bold else FontWeight.SemiBold,
                    color = when {
                        isActive -> colors.foreground
                        index < activeIndex -> colors.mutedForeground.copy(alpha = 0.55f)
                        else -> colors.mutedForeground
                    },
                    textAlign = TextAlign.Center,
                    modifier = Modifier
                        .fillMaxWidth()
                        // Tapping a line is a "play from here" gesture, so it re-engages following.
                        .clickable {
                            onSeek(line.timeMs)
                            followActive = true
                        }
                        .padding(horizontal = 20.dp),
                )
            }
        }

        AnimatedVisibility(
            visible = !followActive,
            enter = fadeIn(),
            exit = fadeOut(),
            modifier = Modifier.align(Alignment.BottomCenter).padding(bottom = 12.dp),
        ) {
            SyncPill { followActive = true }
        }
    }
}

@Composable
private fun SyncPill(onClick: () -> Unit) {
    val colors = MhTheme.colors
    Row(
        modifier = Modifier
            .clip(CircleShape)
            .background(colors.card)
            .border(1.dp, colors.border, CircleShape)
            .clickable(onClick = onClick)
            .padding(horizontal = 14.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(
            Icons.Rounded.VerticalAlignCenter,
            contentDescription = null,
            tint = colors.primary,
            modifier = Modifier.size(15.dp),
        )
        Spacer(Modifier.size(6.dp))
        Text(
            "Sync",
            style = MaterialTheme.typography.labelMedium,
            fontWeight = FontWeight.SemiBold,
            color = colors.foreground,
        )
    }
}

@Composable
private fun LyricsMessage(modifier: Modifier, content: @Composable () -> Unit) {
    Box(modifier = modifier.fillMaxSize(), contentAlignment = Alignment.Center) { content() }
}
