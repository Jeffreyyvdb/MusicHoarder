package com.musichoarder.app.player

import com.musichoarder.app.data.PlaybackMode
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Now Playing's Devices button follows the web's `TrackPanel` (`showDevices = playbackSync.enabled`):
 * on whenever the feature is, not only once a second device happens to be online — otherwise a
 * phone on its own never lets on that Connect exists. "On" has one more condition here than on the
 * web, which ships with its own API: a self-hosted server can be older than the app, so the feature
 * shows once the server has answered a playback call ([SyncSupport]).
 */
class ConnectViewTest {

    @Test
    fun `a lone phone playing its own music still shows Devices`() {
        assertTrue(ConnectView(available = true, mode = PlaybackMode.Local).showsDevices)
    }

    @Test
    fun `not while the feature is off`() {
        // Signed out, the demo, a server without the feature, or a share queue playing here.
        assertFalse(ConnectView(available = false, mode = PlaybackMode.Local).showsDevices)
    }

    @Test
    fun `a server that has not answered yet is asked, but shows nothing`() {
        // A first start, and the retry after a 404: a self-hosted server older than the app 404s
        // again a moment later, and a button shown meanwhile would appear and vanish every time.
        assertTrue(SyncSupport.Unknown.callsServer)
        assertFalse(SyncSupport.Unknown.shown)
        // Shown once a playback call has been answered.
        assertTrue(SyncSupport.Supported.callsServer)
        assertTrue(SyncSupport.Supported.shown)
    }

    @Test
    fun `a refusal neither calls the server nor shows anything`() {
        for (refused in listOf(SyncSupport.Unsupported, SyncSupport.Forbidden)) {
            assertFalse(refused.name, refused.callsServer)
            assertFalse(refused.name, refused.shown)
        }
    }
}
