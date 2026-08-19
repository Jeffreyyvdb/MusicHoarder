package com.musichoarder.app.ui

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * Pins the tint hash to the values `frontend/src/lib/album-tint.ts` produces.
 *
 * The two clients have to agree or the same coverless album gets a different placeholder gradient on
 * each — which is what happened when one multiplier was transcribed as `-0x61c88647` (-1640531527)
 * instead of the signed form of 2654435761 (-0x61c8864f). The bug was invisible because the port
 * still produced stable, plausible colours; only comparing against the original caught it.
 */
class AlbumTintTest {

    @Test
    fun `cyrb53 matches the JavaScript implementation`() {
        assertEquals(6052793779709700L, cyrb53("2pac::better dayz"))
        assertEquals(6463872188666417L, cyrb53("21 savage::savage mode ii"))
        assertEquals(3338908027751811L, cyrb53(""))
        assertEquals(2308849574374730L, cyrb53("a::b"))
    }

    @Test
    fun `hue derived from the hash matches the web`() {
        assertEquals(60, (cyrb53("2pac::better dayz") % 360).toInt())
        assertEquals(137, (cyrb53("21 savage::savage mode ii") % 360).toInt())
    }
}
