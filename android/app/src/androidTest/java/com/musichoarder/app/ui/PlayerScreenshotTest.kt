package com.musichoarder.app.ui

import android.graphics.Bitmap
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.asAndroidBitmap
import androidx.compose.ui.test.captureToImage
import androidx.compose.ui.test.junit4.ComposeContentTestRule
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.onRoot
import androidx.compose.ui.test.performClick
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.musichoarder.app.data.Lyrics
import com.musichoarder.app.data.VideoInfo
import com.musichoarder.app.data.parseLrc
import com.musichoarder.app.player.PlayerUiState
import com.musichoarder.app.player.VideoState
import com.musichoarder.app.ui.theme.MusicHoarderTheme
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

/**
 * Renders each pane of the player and writes a PNG to the device's external cache, so the layout
 * can be compared against the web player without a paired server behind it.
 *
 * This is a screenshot *harness*, not an assertion — it fails only if a pane cannot be composed at
 * all. Pull the output with:
 *
 * ```
 * adb pull /sdcard/Android/data/com.musichoarder.app/files/player-screenshots
 * ```
 *
 * Run it with `am instrument` rather than `connectedDebugAndroidTest`: Gradle uninstalls both APKs
 * when the task finishes, taking the output with them.
 */
@RunWith(AndroidJUnit4::class)
class PlayerScreenshotTest {

    @get:Rule
    val rule = createComposeRule()

    private val playerState = PlayerUiState(
        trackId = 1,
        title = "Bands (feat. OhGeesy, Fenix Flexin & Master Kato)",
        artist = "Shoreline Mafia",
        album = "Shoreline Mafia Presents Rob Vicious: Traplantic",
        hasCover = false,
        isPlaying = true,
        positionMs = 10_000,
        durationMs = 174_000,
        hasNext = true,
        hasPrevious = true,
    )

    // Deliberately not the real song's words: this is a layout fixture, and the shape of the lines
    // is the only thing it needs to get right.
    private val lyrics = LyricsUiState.Ready(
        Lyrics(
            lines = parseLrc(
                """
                [00:00.00]Sample lyric line one
                [00:04.00]A second line, this one long enough to wrap onto two rows
                [00:08.00]Third line
                [00:12.00]Fourth line goes here
                [00:16.00]And a fifth to fill the card
                [00:20.00]Sixth line
                [00:24.00]Seventh line of the fixture
                [00:28.00]Eighth and last
                """.trimIndent()
            ),
            plainText = null,
            isInstrumental = false,
            isTranscribed = false,
        )
    )

    @Test
    fun captureLyricsPane() = capture("01-lyrics") { player(lyrics, VideoState()) }

    @Test
    fun captureSongPane() = capture("02-song", tapTab = "Song") { player(lyrics, VideoState()) }

    @Test
    fun captureVideoPane() = capture(
        "03-video",
        tapTab = "Video",
    ) {
        // hasVideo + isVisible put the Video tab in the strip; the surface itself stays black
        // without a decoder, which is exactly what the letterbox ground should look like.
        player(
            lyrics,
            VideoState(
                songId = 1,
                info = VideoInfo(status = "Ready", durationSeconds = 170),
                isVisible = true,
                aspectRatio = 16f / 9f,
            ),
        )
    }

    @Test
    fun captureFullscreenLyrics() = capture("04-lyrics-fullscreen", tap = { rule ->
        rule.onNodeWithContentDescription("Show fullscreen lyrics").performClick()
    }) { player(lyrics, VideoState()) }

    @Test
    fun captureNoLyrics() = capture("05-no-lyrics") {
        player(LyricsUiState.Failed("No lyrics for this track."), VideoState())
    }

    @Composable
    private fun player(lyricsState: LyricsUiState, videoState: VideoState) {
        MusicHoarderTheme(darkTheme = true) {
            NowPlayingScreen(
                state = playerState,
                coverUrl = null,
                ambientCoverUrl = null,
                lyricsState = lyricsState,
                videoState = videoState,
                isLiked = true,
                showVideoBackdrop = true,
                onToggleVideoBackdrop = {},
                onToggleLike = {},
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

    private fun capture(
        name: String,
        tapTab: String? = null,
        tap: ((ComposeContentTestRule) -> Unit)? = null,
        content: @Composable () -> Unit,
    ) {
        rule.setContent { content() }
        rule.waitForIdle()
        tapTab?.let {
            rule.onNodeWithText(it).performClick()
            rule.waitForIdle()
        }
        tap?.let {
            it(rule)
            rule.waitForIdle()
        }
        val bitmap = rule.onRoot().captureToImage().asAndroidBitmap()
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val dir = File(context.getExternalFilesDir(null) ?: context.filesDir, "player-screenshots")
            .apply { mkdirs() }
        File(dir, "$name.png").outputStream().use { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it) }
    }
}
