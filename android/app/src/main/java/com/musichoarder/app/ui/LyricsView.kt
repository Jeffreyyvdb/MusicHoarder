package com.musichoarder.app.ui

import androidx.activity.compose.BackHandler
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.foundation.background
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
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.GraphicEq
import androidx.compose.material.icons.rounded.KeyboardArrowDown
import androidx.compose.material.icons.rounded.OpenInFull
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
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.musichoarder.app.data.Lyrics
import com.musichoarder.app.player.PlayerUiState
import com.musichoarder.app.ui.theme.MhTheme

/** What the player screen knows about the current song's lyrics. */
sealed interface LyricsUiState {
    data object Loading : LyricsUiState
    data class Ready(val lyrics: Lyrics) : LyricsUiState
    data class Failed(val message: String) : LyricsUiState

    /** Whether there is anything to show — drives the player's default tab, as on the web. */
    val hasLyrics: Boolean
        get() = this is Ready && !lyrics.isEmpty
}

/**
 * The lyric line, in the web's "theater" size: `text-2xl leading-snug font-bold tracking-[-0.01em]`.
 */
private val LyricLineStyle = TextStyle(
    fontFamily = FontFamily.Default,
    fontSize = 24.sp,
    lineHeight = 33.sp,
    fontWeight = FontWeight.Bold,
    letterSpacing = (-0.24).sp,
)

/**
 * The synced lyrics viewer.
 *
 * Auto-scroll keeps the active line centred, but it must not fight the reader: touching the list
 * disengages following and a floating "Sync" pill re-engages it — the same contract the web panel
 * (and every other music player) uses. Disengaging keys off the touch itself rather than the scroll
 * position, because a scroll listener cannot tell our own animated scroll from a finger.
 *
 * Pass a null [onSeek] for a read-only preview: the list stops taking gestures and the pill goes
 * away, so a parent (the lyrics card) can own the tap. That is the web's `pointer-events-none`.
 */
@Composable
fun LyricsView(
    state: LyricsUiState,
    positionMs: Long,
    onSeek: ((Long) -> Unit)?,
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
                        .verticalScroll(rememberScrollState(), enabled = onSeek != null)
                        .padding(horizontal = 8.dp, vertical = 24.dp),
                ) {
                    Text(
                        lyrics.plainText,
                        style = LyricLineStyle,
                        color = colors.foreground.copy(alpha = 0.8f),
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
    onSeek: ((Long) -> Unit)?,
    modifier: Modifier,
) {
    val colors = MhTheme.colors
    val listState = rememberLazyListState()
    var followActive by remember(lyrics) { mutableStateOf(true) }
    val interactive = onSeek != null

    // Last line whose timestamp has passed; -1 before the first.
    val activeIndex = remember(lyrics, positionMs) {
        var active = -1
        for (i in lyrics.lines.indices) {
            if (lyrics.lines[i].timeMs <= positionMs) active = i else break
        }
        active
    }
    // Before the first timestamp there is nothing to highlight, so the whole document sits at one
    // even weight rather than being dimmed as if it had all gone past. Same as the web's untracked
    // state, which it reaches whenever the panel is open on a song that is not the loaded one.
    val isTracking = activeIndex >= 0

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
            userScrollEnabled = interactive,
            contentPadding = PaddingValues(top = halfViewport, bottom = halfViewport),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(4.dp),
            modifier = Modifier
                .fillMaxSize()
                // Watch the raw touch stream: any finger down on the list means the reader has taken
                // over. Initial pass so it fires before the scroll gesture consumes the event.
                .pointerInput(interactive) {
                    if (!interactive) return@pointerInput
                    awaitEachGesture {
                        awaitFirstDown(requireUnconsumed = false, pass = PointerEventPass.Initial)
                        followActive = false
                    }
                },
        ) {
            itemsIndexed(lyrics.lines) { index, line ->
                val target = when {
                    !isTracking -> colors.foreground.copy(alpha = 0.8f)
                    index == activeIndex -> colors.foreground
                    // Past and future dim to the same weight on the web — only "now" is bright.
                    else -> colors.foreground.copy(alpha = 0.3f)
                }
                val color by animateColorAsState(target, tween(300), label = "lyric-line")
                Text(
                    text = line.text.ifBlank { "♪" },
                    style = LyricLineStyle,
                    color = color,
                    textAlign = TextAlign.Center,
                    modifier = Modifier
                        .fillMaxWidth()
                        .then(
                            // Tapping a line is a "play from here" gesture, so it re-engages follow.
                            if (onSeek == null) Modifier else Modifier.clickable {
                                onSeek(line.timeMs)
                                followActive = true
                            }
                        )
                        .padding(horizontal = 20.dp, vertical = 6.dp),
                )
            }
        }

        AnimatedVisibility(
            visible = interactive && !followActive,
            enter = fadeIn(tween(120)),
            exit = fadeOut(tween(120)),
            modifier = Modifier.align(Alignment.BottomCenter).padding(bottom = 12.dp),
        ) {
            SyncPill { followActive = true }
        }
    }
}

/** `bg-foreground text-background rounded-full` — the web's inverted re-engage pill. */
@Composable
private fun SyncPill(onClick: () -> Unit) {
    val colors = MhTheme.colors
    Row(
        modifier = Modifier
            .clip(CircleShape)
            .background(colors.foreground)
            .clickable(onClick = onClick)
            .padding(horizontal = 16.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(
            Icons.Rounded.GraphicEq,
            contentDescription = null,
            tint = colors.background,
            modifier = Modifier.size(16.dp),
        )
        Spacer(Modifier.size(6.dp))
        Text(
            "Sync",
            style = MaterialTheme.typography.bodyMedium,
            fontWeight = FontWeight.SemiBold,
            color = colors.background,
        )
    }
}

/**
 * The mobile lyrics preview: a live karaoke window that expands to [LyricsFullscreen].
 *
 * The viewer inside keeps following the song but takes no gestures, so the whole card is one tap
 * target — the web does the same with `pointer-events-none` on the panel.
 */
@Composable
fun LyricsCard(
    state: LyricsUiState,
    positionMs: Long,
    onExpand: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val colors = MhTheme.colors
    Column(
        modifier = modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(16.dp))
            .background(colors.foreground.copy(alpha = 0.05f))
            .clickable(onClick = onExpand),
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(start = 16.dp, end = 16.dp, top = 14.dp, bottom = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(
                "LYRICS",
                style = MaterialTheme.typography.bodySmall,
                fontWeight = FontWeight.SemiBold,
                letterSpacing = 1.2.sp,
                color = colors.mutedForeground,
                modifier = Modifier.weight(1f),
            )
            Icon(
                Icons.Rounded.OpenInFull,
                contentDescription = "Show fullscreen lyrics",
                tint = colors.mutedForeground,
                modifier = Modifier.size(16.dp),
            )
        }
        Box(modifier = Modifier.fillMaxWidth().height(288.dp).padding(horizontal = 12.dp, vertical = 12.dp)) {
            LyricsView(state = state, positionMs = positionMs, onSeek = null)
            // Bottom fade, hinting there is more to see fullscreen.
            Box(
                modifier = Modifier
                    .align(Alignment.BottomCenter)
                    .fillMaxWidth()
                    .height(48.dp)
                    .background(
                        Brush.verticalGradient(
                            0f to Color.Transparent,
                            1f to colors.background.copy(alpha = 0.25f),
                        )
                    )
            )
        }
    }
}

/**
 * The fullscreen lyrics overlay: just the words over the track's ambient artwork, with a mini
 * header and a scrubber + play bottom bar. Mount it conditionally — it owns its own back handling
 * so that Back closes the overlay rather than the player behind it.
 */
@Composable
fun LyricsFullscreen(
    state: LyricsUiState,
    playerState: PlayerUiState,
    coverUrl: String?,
    ambientUrl: String?,
    onPlayPause: () -> Unit,
    onSeek: (Long) -> Unit,
    onSetSpeed: (Float) -> Unit,
    onClose: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val colors = MhTheme.colors
    BackHandler(onBack = onClose)

    Box(modifier = modifier.fillMaxSize().background(colors.background)) {
        AmbientBackdrop(
            url = ambientUrl,
            artist = playerState.artist,
            title = playerState.album.ifBlank { playerState.title },
            scrimAlpha = 0.85f,
            modifier = Modifier.fillMaxSize(),
        )

        Column(
            modifier = Modifier
                .fillMaxSize()
                .statusBarsPadding()
                .navigationBarsPadding()
                .padding(horizontal = 20.dp),
        ) {
            Row(
                modifier = Modifier.fillMaxWidth().padding(top = 16.dp, bottom = 12.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                Artwork(
                    url = coverUrl,
                    artist = playerState.artist,
                    title = playerState.album.ifBlank { playerState.title },
                    modifier = Modifier.size(44.dp),
                    shape = RoundedCornerShape(8.dp),
                )
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        playerState.title,
                        style = legible(MaterialTheme.typography.bodyLarge, onSurface = true),
                        fontWeight = FontWeight.SemiBold,
                        color = colors.foreground,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                    Text(
                        playerState.artist,
                        style = legible(MaterialTheme.typography.bodySmall, onSurface = true),
                        color = colors.mutedForeground,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
                MhCircleIconButton(
                    icon = Icons.Rounded.KeyboardArrowDown,
                    contentDescription = "Close fullscreen lyrics",
                    onClick = onClose,
                    groundAlpha = 0.1f,
                    iconSize = 20.dp,
                )
            }

            LyricsView(
                state = state,
                positionMs = playerState.positionMs,
                onSeek = onSeek,
                modifier = Modifier.weight(1f),
            )

            PlayerTransport(
                state = playerState,
                onPlayPause = onPlayPause,
                onNext = {},
                onPrevious = {},
                onSeek = onSeek,
                onSetSpeed = onSetSpeed,
                minimal = true,
                onSurface = true,
                modifier = Modifier.padding(top = 8.dp, bottom = 20.dp),
            )
        }
    }
}

@Composable
private fun LyricsMessage(modifier: Modifier, content: @Composable () -> Unit) {
    Box(modifier = modifier.fillMaxSize(), contentAlignment = Alignment.Center) { content() }
}
