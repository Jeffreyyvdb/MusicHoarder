package com.musichoarder.app.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/**
 * The share/invite deep-link grammar: exactly `http(s)://host[:port]/<share|invite>/<token>`.
 * Anything looser would swallow the authenticated `/shared` routes or a bare `/share` page.
 */
@RunWith(RobolectricTestRunner::class)
// android.net.Uri is stubbed in the plain unit-test android.jar, so parsing needs Robolectric.
@Config(sdk = [34])
class ShareLinkTest {

    @Test
    fun `parses an https share link`() {
        val link = ShareLink.parse("https://musichoarder.app/share/abc123_-XY")
        assertEquals(ShareLink("https://musichoarder.app", "abc123_-XY"), link)
    }

    @Test
    fun `keeps an explicit port and http scheme for LAN instances`() {
        val link = ShareLink.parse("http://192.168.1.10:5173/share/tok")
        assertEquals(ShareLink("http://192.168.1.10:5173", "tok"), link)
    }

    @Test
    fun `ignores query and fragment`() {
        val link = ShareLink.parse("https://musichoarder.app/share/tok?utm_source=x#top")
        assertEquals(ShareLink("https://musichoarder.app", "tok"), link)
    }

    @Test
    fun `trims surrounding whitespace`() {
        val link = ShareLink.parse("  https://musichoarder.app/share/tok  ")
        assertEquals(ShareLink("https://musichoarder.app", "tok"), link)
    }

    @Test
    fun `rejects everything that is not exactly a share path`() {
        assertNull(ShareLink.parse("https://musichoarder.app/shared/songs"))
        assertNull(ShareLink.parse("https://musichoarder.app/share"))
        assertNull(ShareLink.parse("https://musichoarder.app/share/"))
        assertNull(ShareLink.parse("https://musichoarder.app/share/a/b"))
        assertNull(ShareLink.parse("https://musichoarder.app/invite/tok"))
        assertNull(ShareLink.parse("musichoarder://pair?v=1&url=x&token=y"))
        assertNull(ShareLink.parse("ftp://musichoarder.app/share/tok"))
        assertNull(ShareLink.parse("not a url"))
        assertNull(ShareLink.parse(""))
    }

    @Test
    fun `parses an invite link with the same grammar`() {
        val link = InviteLink.parse("https://musichoarder.app/invite/tok")
        assertEquals(InviteLink("https://musichoarder.app", "tok"), link)
        assertNull(InviteLink.parse("https://musichoarder.app/share/tok"))
        assertNull(InviteLink.parse("https://musichoarder.app/invite"))
        assertNull(InviteLink.parse("https://musichoarder.app/invite/a/b"))
    }
}
