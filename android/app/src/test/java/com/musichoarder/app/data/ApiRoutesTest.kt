package com.musichoarder.app.data

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * Pins the API paths verbatim. A drifted path here silently turns the phone into an empty library,
 * or points it at an endpoint that 403s, and neither failure is visible until someone opens the
 * app.
 *
 * These used to be owner ↔ shared PAIRS, because an invited account read through a parallel
 * `/api/shared` surface. The server now scopes the ordinary endpoints to the caller, so there is
 * one path per operation and the client no longer branches on what kind of account it holds — the
 * disappearance of that second column is the point of this file's current shape.
 */
class ApiRoutesTest {
    @Test
    fun `every route is the single caller-scoped endpoint`() {
        assertEquals("/songs", ApiRoutes.songs())
        assertEquals("/songs/7/stream", ApiRoutes.stream(7))
        assertEquals("/songs/7/cover?size=256", ApiRoutes.cover(7, 256))
        assertEquals("/api/tracks/7/lyrics", ApiRoutes.lyrics(7))
        assertEquals("/songs/7/video", ApiRoutes.video(7))
        assertEquals("/songs/7/video/stream", ApiRoutes.videoStream(7))
        assertEquals("/songs/7/like", ApiRoutes.like(7))
        assertEquals("/songs/7/played", ApiRoutes.played(7))
    }

    @Test
    fun `no route points at the deprecated shared surface`() {
        val all = listOf(
            ApiRoutes.songs(),
            ApiRoutes.stream(7),
            ApiRoutes.cover(7, 256),
            ApiRoutes.lyrics(7),
            ApiRoutes.video(7),
            ApiRoutes.videoStream(7),
            ApiRoutes.like(7),
            ApiRoutes.played(7),
        )
        // /api/shared is kept alive server-side for one release so already-installed builds keep
        // working. A NEW build must not use it, or it will break when those routes are deleted.
        assertEquals(emptyList<String>(), all.filter { it.startsWith("/api/shared") })
    }
}
