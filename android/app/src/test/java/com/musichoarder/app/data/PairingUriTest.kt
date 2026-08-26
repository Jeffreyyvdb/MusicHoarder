package com.musichoarder.app.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/**
 * The server address the sign-in screen resolves before any request goes out.
 *
 * [PairingUri.DEFAULT_BASE_URL] is what every button on that screen uses until someone taps
 * Change, so it has to survive [PairingUri.normalizeBaseUrl] untouched — a default that normalized
 * to something else (or to null) would send first-run traffic somewhere nobody chose.
 */
@RunWith(RobolectricTestRunner::class)
// android.net.Uri is stubbed in the plain unit-test android.jar, so parsing needs Robolectric.
@Config(sdk = [34])
class PairingUriTest {

    @Test
    fun `the default server is already a normalized origin`() {
        assertEquals(
            PairingUri.DEFAULT_BASE_URL,
            PairingUri.normalizeBaseUrl(PairingUri.DEFAULT_BASE_URL),
        )
    }

    @Test
    fun `an override may be typed the way people say it`() {
        assertEquals("https://musichoarder.app", PairingUri.normalizeBaseUrl("musichoarder.app"))
        assertEquals("https://musichoarder.app", PairingUri.normalizeBaseUrl("  musichoarder.app/ "))
        assertEquals("http://192.168.1.10:3000", PairingUri.normalizeBaseUrl("http://192.168.1.10:3000/"))
    }

    @Test
    fun `an empty override is not a server`() {
        assertNull(PairingUri.normalizeBaseUrl(""))
        assertNull(PairingUri.normalizeBaseUrl("   "))
        assertNull(PairingUri.normalizeBaseUrl("https://"))
    }
}
