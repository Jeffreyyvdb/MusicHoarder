package com.musichoarder.app.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins the owner ↔ shared route pairs. The shared paths are the grant-scoped surface a Friend
 * pairing reads; a drifted path here silently turns a friend's phone into an empty library (or
 * worse, points it at owner endpoints that 403), so every pair is asserted verbatim.
 */
class ApiRoutesTest {
    @Test
    fun `owner routes are the tenancy-filtered endpoints`() {
        assertEquals("/songs", ApiRoutes.songs(friend = false))
        assertEquals("/songs/7/stream", ApiRoutes.stream(7, friend = false))
        assertEquals("/songs/7/cover?size=256", ApiRoutes.cover(7, 256, friend = false))
        assertEquals("/api/tracks/7/lyrics", ApiRoutes.lyrics(7, friend = false))
        assertEquals("/songs/7/video", ApiRoutes.video(7, friend = false))
        assertEquals("/songs/7/video/stream", ApiRoutes.videoStream(7, friend = false))
        assertEquals("/songs/7/like", ApiRoutes.like(7, friend = false))
        assertEquals("/songs/7/played", ApiRoutes.played(7, friend = false))
    }

    @Test
    fun `friend routes are the grant-scoped shared endpoints`() {
        assertEquals("/api/shared/songs", ApiRoutes.songs(friend = true))
        assertEquals("/api/shared/songs/7/stream", ApiRoutes.stream(7, friend = true))
        assertEquals("/api/shared/songs/7/cover?size=256", ApiRoutes.cover(7, 256, friend = true))
        assertEquals("/api/shared/songs/7/lyrics", ApiRoutes.lyrics(7, friend = true))
        assertEquals("/api/shared/songs/7/video", ApiRoutes.video(7, friend = true))
        assertEquals("/api/shared/songs/7/video/stream", ApiRoutes.videoStream(7, friend = true))
        assertEquals("/api/shared/songs/7/like", ApiRoutes.like(7, friend = true))
        assertEquals("/api/shared/songs/7/played", ApiRoutes.played(7, friend = true))
    }

    @Test
    fun `friend flag comes from the session role, case-insensitively`() {
        assertTrue(ServerSession("https://h", "t", role = "Friend").isFriend)
        assertTrue(ServerSession("https://h", "t", role = "friend").isFriend)
        assertFalse(ServerSession("https://h", "t", role = "Owner").isFriend)
        assertFalse(ServerSession("https://h", "t", role = "Demo").isFriend)
        // Phones paired before roles existed have no stored role — they keep owner behaviour.
        assertFalse(ServerSession("https://h", "t", role = null).isFriend)
    }
}
