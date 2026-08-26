package com.musichoarder.app.ui

import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.test.SemanticsNodeInteractionCollection
import androidx.compose.ui.test.assertCountEquals
import androidx.compose.ui.test.filter
import androidx.compose.ui.test.hasClickAction
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onChildren
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.musichoarder.app.player.PlayerUiState
import com.musichoarder.app.player.VideoState
import com.musichoarder.app.ui.theme.MusicHoarderTheme
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * The player's `artist · album` line is two links, the way the web panel's is.
 *
 * Asserted on the real Compose runtime rather than by reading the builder back: the halves only
 * become separate tap targets because each is a `LinkAnnotation` inside one [androidx.compose
 * .material3.Text], and that is the part worth pinning — the line has to keep truncating as a
 * whole while still handing the two names their own click actions.
 *
 * Test names stay camelCase here: a backticked one needs DEX 040, and this module's minSdk is 24.
 */
@RunWith(AndroidJUnit4::class)
class NowPlayingLinkTapTest {

    @get:Rule
    val rule = createComposeRule()

    private val subtitle = "Trippie Redd, Drake · Trip at Knight"

    private var artistTaps = 0
    private var albumTaps = 0

    @Test
    fun artistHalfOpensTheArtist() {
        rule.setContent { player(onOpenArtist = { artistTaps++ }, onOpenAlbum = { albumTaps++ }) }
        links()[0].performClick()
        assertEquals(1, artistTaps)
        assertEquals(0, albumTaps)
    }

    @Test
    fun albumHalfOpensTheAlbum() {
        rule.setContent { player(onOpenArtist = { artistTaps++ }, onOpenAlbum = { albumTaps++ }) }
        links()[1].performClick()
        assertEquals(1, albumTaps)
        assertEquals(0, artistTaps)
    }

    @Test
    fun aTrackThisLibraryCannotAnswerForLeavesTheLineAsPlainText() {
        rule.setContent { player(onOpenArtist = null, onOpenAlbum = null) }
        // The line still reads the same — it just has nothing to tap.
        rule.onNodeWithText(subtitle).assertExists()
        links().assertCountEquals(0)
    }

    /** The line's clickable halves, in reading order: artist first, then album. */
    private fun links(): SemanticsNodeInteractionCollection {
        rule.waitForIdle()
        return rule.onNodeWithText(subtitle).onChildren().filter(hasClickAction())
    }

    @Composable
    private fun player(onOpenArtist: (() -> Unit)?, onOpenAlbum: (() -> Unit)?) {
        MusicHoarderTheme(darkTheme = true) {
            NowPlayingScreen(
                state = PlayerUiState(
                    trackId = 1,
                    title = "Betrayal",
                    artist = "Trippie Redd, Drake",
                    album = "Trip at Knight",
                    isPlaying = true,
                    durationMs = 174_000,
                ),
                coverUrl = null,
                ambientCoverUrl = null,
                lyricsState = LyricsUiState.Failed("No lyrics for this track."),
                videoState = VideoState(),
                isLiked = false,
                showVideoBackdrop = false,
                onToggleVideoBackdrop = {},
                onToggleLike = {},
                onOpenArtist = onOpenArtist,
                onOpenAlbum = onOpenAlbum,
                onCollapse = {},
                onPlayPause = {},
                onNext = {},
                onPrevious = {},
                onSeek = {},
                onSetSpeed = {},
                onToggleShuffle = {},
                onCycleRepeat = {},
                onAttachVideoSurface = {},
                onDetachVideoSurface = {},
                modifier = Modifier.fillMaxSize(),
            )
        }
    }
}
