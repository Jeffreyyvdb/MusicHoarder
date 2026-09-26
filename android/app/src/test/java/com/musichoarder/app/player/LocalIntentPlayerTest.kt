package com.musichoarder.app.player

import androidx.annotation.OptIn
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.test.utils.TestExoPlayerBuilder
import com.musichoarder.app.player.PlaybackConnect.HereAction
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

/**
 * The wrapper the media session drives ([LocalIntentPlayer]), over a real ExoPlayer.
 *
 * What it pins is the media-key rule both clients share: while another device's session (or a
 * remembered one) is shown, the notification's, lock screen's or a headset's Play, Next and
 * Previous pick *that* session up here (the web's `mediaKey` → `playHere`) and leave the player's
 * own leftovers alone — resuming them would claim the account's session for a queue nobody is
 * listening to. A row tap, which loads a queue and then plays it, is still a claim of that queue.
 */
@OptIn(UnstableApi::class)
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34])
class LocalIntentPlayerTest {

    private class Intents : LocalIntents {
        /** What [pickUpInstead] answers: true while the UI shows the session of another device. */
        var sessionShownElsewhere = false
        val intents = mutableListOf<Boolean>()
        val pickUps = mutableListOf<HereAction>()

        /** The actions handed over with each pick-up, to carry out on the player after all. */
        val handedBack = mutableListOf<() -> Unit>()
        var replaced = 0

        override fun onIntent(play: Boolean) {
            intents += play
        }

        override fun pickUpInstead(action: HereAction, local: () -> Unit): Boolean {
            if (sessionShownElsewhere) {
                pickUps += action
                handedBack += local
            }
            return sessionShownElsewhere
        }

        override fun beforeQueueReplaced() {
            replaced++
        }
    }

    private lateinit var exo: ExoPlayer
    private val intents = Intents()
    private lateinit var player: LocalIntentPlayer

    @Before
    fun setUp() {
        exo = TestExoPlayerBuilder(RuntimeEnvironment.getApplication()).build()
        player = LocalIntentPlayer(exo, intents)
    }

    @After
    fun tearDown() {
        exo.release()
    }

    private fun item(id: Int, share: Boolean = false): MediaItem = MediaItem.Builder()
        .setMediaId(id.toString())
        .setUri("https://music.invalid/api/songs/$id/stream")
        .setMediaMetadata(MediaMetadata.Builder().apply { if (share) setExtras(shareItemExtras()) }.build())
        .build()

    /** A queue this phone played, then paused when another device took the session. */
    private fun leftovers() {
        player.setMediaItems(mutableListOf(item(1), item(2), item(3)))
        player.play()
        exo.pause()
        intents.intents.clear()
        intents.sessionShownElsewhere = true
    }

    @Test
    fun `Play on the hidden leftovers picks the session up instead`() {
        leftovers()

        player.play()
        player.playWhenReady = true

        assertFalse("the leftovers must stay paused", exo.playWhenReady)
        assertEquals(listOf(HereAction.Resume, HereAction.Resume), intents.pickUps)
        assertEquals("nothing claims them", emptyList<Boolean>(), intents.intents)
    }

    @Test
    fun `Next and Previous pick it up with their step`() {
        leftovers()

        player.seekToNextMediaItem()
        player.seekToNext()
        player.seekToPrevious()
        player.seekToPreviousMediaItem()

        assertEquals(0, exo.currentMediaItemIndex)
        assertEquals(
            listOf(HereAction.Next, HereAction.Next, HereAction.Previous, HereAction.Previous),
            intents.pickUps,
        )
    }

    @Test
    fun `a queue a controller has just loaded plays, and claims`() {
        leftovers()

        // A row tap: set, then play.
        player.setMediaItems(mutableListOf(item(7), item(8)), 1, 0L)
        player.play()

        assertTrue(exo.playWhenReady)
        assertEquals(listOf(true), intents.intents)
        assertEquals(emptyList<HereAction>(), intents.pickUps)
        // Once played, that queue is simply this phone's: the next Play asks again.
        exo.pause()
        player.play()
        assertEquals(listOf(HereAction.Resume), intents.pickUps)
    }

    @Test
    fun `plays its own queue when the music is here`() {
        player.setMediaItems(mutableListOf(item(1), item(2)))
        player.play()
        exo.pause()
        player.play()
        player.seekToNextMediaItem()

        assertTrue(exo.playWhenReady)
        assertEquals(1, exo.currentMediaItemIndex)
        assertEquals(listOf(true, true, false), intents.intents)
    }

    @Test
    fun `a pick-up that finds the music here plays this player after all, and claims it`() {
        // A headset's Play with the stream closed: the session is asked about first, and it turns
        // out to be this phone's.
        leftovers()
        player.play()
        player.seekToNextMediaItem()
        assertFalse(exo.playWhenReady)
        assertEquals(0, exo.currentMediaItemIndex)

        intents.handedBack.forEach { it() }

        assertTrue(exo.playWhenReady)
        assertEquals(1, exo.currentMediaItemIndex)
        // Each carried out once, as the local intent it was — not asked about a second time.
        assertEquals(listOf(true, false), intents.intents)
        assertEquals(listOf(HereAction.Resume, HereAction.Next), intents.pickUps)
        // And the question is asked again next time.
        exo.pause()
        player.play()
        assertEquals(listOf(HereAction.Resume, HereAction.Next, HereAction.Resume), intents.pickUps)
    }

    @Test
    fun `a seek is never a pick-up`() {
        leftovers()

        player.seekTo(1_000L)

        assertEquals(emptyList<HereAction>(), intents.pickUps)
        assertEquals(listOf(false), intents.intents)
    }

    @Test
    fun `every queue a controller loads is announced before it lands`() {
        player.setMediaItems(mutableListOf(item(1)))
        player.setMediaItem(item(2))
        player.setMediaItem(item(3), 0L)
        assertEquals(3, intents.replaced)
    }

    @Test
    fun `only a library track playing here can be superseded`() {
        exo.setMediaItem(item(5, share = true))
        exo.playWhenReady = true
        assertFalse(exo.playsSessionHere())

        exo.setMediaItem(item(5))
        assertTrue(exo.playsSessionHere())

        exo.pause()
        assertFalse(exo.playsSessionHere())
    }
}
