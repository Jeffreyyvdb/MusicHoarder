package com.musichoarder.app.player

import androidx.annotation.OptIn
import androidx.media3.common.MediaMetadata
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.test.utils.TestExoPlayerBuilder
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

/**
 * The station seed rides in the player's playlist metadata ([stationMetadata] / [stationSeedId]),
 * and a player drops a "change" to metadata that `equals` the current one. `MediaMetadata.equals`
 * compares extras only by whether they are null, so a seed that lived in the extras alone was
 * stuck on the first pick: every later station was built from it, and every report sent it.
 *
 * Needs the real Android runtime — `MediaMetadata.equals` goes through `TextUtils.equals`, which the
 * stubbed android.jar answers with a default — and a real ExoPlayer, whose early return on equal
 * metadata is the thing that bit. The MediaController on the app's side masks with the same
 * `equals`, so the equality cases stand in for it.
 */
@OptIn(UnstableApi::class)
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34])
class StationSeedTest {

    private lateinit var player: ExoPlayer

    @Before
    fun setUp() {
        player = TestExoPlayerBuilder(RuntimeEnvironment.getApplication()).build()
    }

    @After
    fun tearDown() {
        player.release()
    }

    @Test
    fun `a new pick reseeds the station`() {
        player.playlistMetadata = stationMetadata(1)
        assertEquals(1, player.stationSeedId)

        player.playlistMetadata = stationMetadata(2)
        assertEquals(2, player.stationSeedId)

        // And back again: an adopted session can carry an earlier seed.
        player.playlistMetadata = stationMetadata(1)
        assertEquals(1, player.stationSeedId)
    }

    @Test
    fun `two seeds never compare equal, one seed always does`() {
        assertNotEquals(stationMetadata(1), stationMetadata(2))
        assertEquals(stationMetadata(7), stationMetadata(7))
    }

    @Test
    fun `no seed clears the station`() {
        player.playlistMetadata = stationMetadata(1)
        player.playlistMetadata = stationMetadata(null)
        assertNull(player.stationSeedId)
    }

    @Test
    fun `the seed survives the trip between the app and the service`() {
        // What the MediaController and the session exchange is the bundle, not the object.
        player.playlistMetadata = stationMetadata(3)
        player.playlistMetadata = MediaMetadata.fromBundle(stationMetadata(4).toBundle())
        assertEquals(4, player.stationSeedId)
    }
}
