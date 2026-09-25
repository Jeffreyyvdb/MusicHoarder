package com.musichoarder.app.ui

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
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.AutoAwesome
import androidx.compose.material.icons.rounded.GraphicEq
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
import androidx.compose.ui.draw.drawWithContent
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.BlendMode
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.CompositingStrategy
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.musichoarder.app.data.Lyrics
import com.musichoarder.app.data.LyricsProvenance
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
 * The lyric line, in the web's "theater" size: `text-title-1 font-bold` (28/34), left-aligned the
 * way Apple Music sets it — a ragged right edge reads as verse, a centred column as a poster.
 */
private val LyricLineStyle = TextStyle(
    fontFamily = FontFamily.Default,
    fontSize = 28.sp,
    lineHeight = 34.sp,
    fontWeight = FontWeight.Bold,
    letterSpacing = (-0.3).sp,
)

/**
 * The synced lyrics viewer — the player's Lyrics mode, the web's `LyricsPanel variant="theater"`.
 * It lives inside Now Playing's media appearance, so it is written for white over the dimmed cover.
 *
 * Auto-scroll keeps the active line in the upper third, but it must not fight the reader: touching
 * the list disengages following and a floating "Sync" pill re-engages it — the same contract the
 * web panel (and every other music player) uses. Disengaging keys off the touch itself rather than
 * the scroll position, because a scroll listener cannot tell our own animated scroll from a finger.
 *
 * Pass a null [onSeek] for a read-only preview: the list stops taking gestures and the pill goes
 * away, so a parent can own the tap. That is the web's `pointer-events-none`.
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

                lyrics.isSynced -> Column(modifier.fillMaxSize()) {
                    AiLyricsBadge(lyrics.provenance)
                    SyncedLyrics(lyrics, positionMs, onSeek, Modifier.weight(1f))
                }

                // Untimed lyrics have nothing to follow, so they read at full contrast — dimming a
                // whole document would only make it look unsynced *and* hard to read.
                !lyrics.plainText.isNullOrBlank() -> Column(
                    modifier = modifier
                        .fillMaxSize()
                        .fadingEdges()
                        .verticalScroll(rememberScrollState(), enabled = onSeek != null)
                        .padding(vertical = 24.dp),
                ) {
                    AiLyricsBadge(lyrics.provenance)
                    Text(
                        lyrics.plainText,
                        style = LyricLineStyle,
                        color = colors.foreground,
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

/**
 * The AI disclosure, mirroring the web's `AiLyricsBadge`: a quiet pill above the lyrics saying who
 * actually wrote the words being read.
 *
 * Two labels, deliberately not one. "AI enhanced" means a machine only moved the timestamps under the
 * song's real lyric; "AI generated" means a machine chose the words themselves and may have them
 * wrong. Sentence case and the same words as the web's `AI_LYRICS_COPY`. Human lyrics render nothing at all — a badge on every song would teach people to ignore it.
 */
@Composable
private fun AiLyricsBadge(provenance: LyricsProvenance, modifier: Modifier = Modifier) {
    if (provenance == LyricsProvenance.Human) return

    val colors = MhTheme.colors
    val label = when (provenance) {
        LyricsProvenance.AiEnhanced -> "AI enhanced"
        LyricsProvenance.AiGenerated -> "AI generated"
        LyricsProvenance.Human -> return
    }
    val description = when (provenance) {
        LyricsProvenance.AiEnhanced ->
            "These are the song's own lyrics. Only the timing was adjusted by AI, to line the words " +
                "up with this recording."
        LyricsProvenance.AiGenerated ->
            "An AI transcribed these lyrics from the audio. No published lyrics were available for " +
                "this track, so the words may be wrong."
        LyricsProvenance.Human -> return
    }

    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = modifier
            .fillMaxWidth()
            .padding(vertical = 8.dp)
            .semantics(mergeDescendants = true) { contentDescription = "$label. $description" },
    ) {
        Row(
            horizontalArrangement = Arrangement.spacedBy(4.dp),
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier
                .clip(CircleShape)
                .background(colors.secondary)
                .padding(horizontal = 10.dp, vertical = 4.dp),
        ) {
            Icon(
                Icons.Rounded.AutoAwesome,
                contentDescription = null,
                tint = colors.mutedForeground,
                modifier = Modifier.size(12.dp),
            )
            Text(
                label,
                style = MaterialTheme.typography.labelMedium,
                color = colors.mutedForeground,
            )
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
    val inactive = colors.foreground.copy(alpha = LYRIC_INACTIVE_ALPHA)
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
    BoxWithConstraints(modifier = modifier.fillMaxSize()) {
        // The active line rides in the upper third, where the eye already is under the compact
        // header, with the lines still to come filling the rest. Scrolling an item to the top of
        // the list puts it just below the top padding, so that padding IS the anchor; the bottom
        // padding lets the last line climb up to it too.
        val anchor = maxHeight * 0.3f

        LaunchedEffect(activeIndex, followActive) {
            if (!followActive || activeIndex < 0) return@LaunchedEffect
            listState.animateScrollToItem(activeIndex)
        }

        LazyColumn(
            state = listState,
            userScrollEnabled = interactive,
            contentPadding = PaddingValues(top = anchor, bottom = maxHeight - anchor),
            modifier = Modifier
                .fillMaxSize()
                .fadingEdges()
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
                // Past and future dim to the same weight on the web — only "now" is bright. Before the
                // first timestamp (activeIndex = -1) every line is still to come, so the whole document
                // dims; at full weight it would read as lyrics that are not synced at all. The dim is
                // the media appearance's 55% white: over the dimmed cover that is still 4.2:1.
                val target = if (index == activeIndex) colors.foreground else inactive
                val color by animateColorAsState(target, tween(300), label = "lyric-line")
                Text(
                    text = line.text.ifBlank { "♪" },
                    style = LyricLineStyle,
                    color = color,
                    modifier = Modifier
                        .fillMaxWidth()
                        .then(
                            // Tapping a line is a "play from here" gesture, so it re-engages follow.
                            if (onSeek == null) Modifier else Modifier.clickable(
                                onClickLabel = "Play from this line",
                                role = Role.Button,
                            ) {
                                onSeek(line.timeMs)
                                followActive = true
                            }
                        )
                        .padding(vertical = 6.dp),
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

/** `bg-foreground text-background rounded-full h-11` — the web's inverted re-engage pill. */
@Composable
private fun SyncPill(onClick: () -> Unit) {
    val colors = MhTheme.colors
    Row(
        modifier = Modifier
            .shadow(8.dp, CircleShape)
            .clip(CircleShape)
            .background(colors.foreground)
            .clickable(onClickLabel = "Follow the song again", role = Role.Button, onClick = onClick)
            .heightIn(min = 44.dp)
            .padding(horizontal = 16.dp),
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

@Composable
private fun LyricsMessage(modifier: Modifier, content: @Composable () -> Unit) {
    Box(modifier = modifier.fillMaxSize(), contentAlignment = Alignment.Center) { content() }
}

/**
 * The web's `inactive` lyric tone, `text-foreground/55` — one of the player's sanctioned vibrancy
 * tokens (see `MediaColors.kt`), only ever drawn over its dimmed cover.
 */
private const val LYRIC_INACTIVE_ALPHA = 0.55f

/**
 * Fades lines out under the chrome at both edges instead of cutting them off at a hard line, as
 * Apple Music's lyrics (and the web's `mask-image`) do. Offscreen compositing so the gradient masks
 * the text itself rather than painting black over the wash behind it.
 */
private fun Modifier.fadingEdges(top: Dp = 24.dp, bottom: Dp = 40.dp): Modifier =
    graphicsLayer { compositingStrategy = CompositingStrategy.Offscreen }
        .drawWithContent {
            drawContent()
            val height = size.height
            if (height <= 0f) return@drawWithContent
            val topStop = (top.toPx() / height).coerceIn(0f, 0.5f)
            val bottomStop = 1f - (bottom.toPx() / height).coerceIn(0f, 0.5f)
            drawRect(
                brush = Brush.verticalGradient(
                    0f to Color.Transparent,
                    topStop to Color.Black,
                    bottomStop to Color.Black,
                    1f to Color.Transparent,
                ),
                blendMode = BlendMode.DstIn,
            )
        }
