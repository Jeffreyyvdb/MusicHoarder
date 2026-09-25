package com.musichoarder.app.data

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * Pins the row tap rule both clients share (web: `TrackList`'s compact `onActivate`): the loaded
 * row brings the player up and never restarts the song; every other row plays the list from there.
 */
class RowTapTest {

    @Test
    fun `a row that is not loaded plays the list from that row`() {
        assertEquals(RowTap.PlayFromRow, rowTapFor(tappedId = 7, loadedId = 3, sameQueueKind = true))
    }

    @Test
    fun `the loaded row opens the player instead of restarting the song`() {
        assertEquals(RowTap.OpenPlayer, rowTapFor(tappedId = 7, loadedId = 7, sameQueueKind = true))
    }

    @Test
    fun `nothing loaded means every row plays`() {
        assertEquals(RowTap.PlayFromRow, rowTapFor(tappedId = 7, loadedId = null, sameQueueKind = true))
    }

    @Test
    fun `a share track with a colliding id does not count as the loaded library row`() {
        // A share queue is playing the sharing server's song 7; the library's own song 7 is a
        // different record, so tapping it has to play it rather than surface the share.
        assertEquals(RowTap.PlayFromRow, rowTapFor(tappedId = 7, loadedId = 7, sameQueueKind = false))
    }
}
