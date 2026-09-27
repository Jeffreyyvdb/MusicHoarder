package com.musichoarder.app.data

import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Test

/**
 * Pins the playlist wire contract (`PlaylistsEndpoints.PlaylistDto`), the id join, and the wording —
 * the last case for case against the web's `$lib/playlists.test.ts`, so the two clients describe a
 * playlist the same way.
 */
class PlaylistsTest {

    private val json = Json { ignoreUnknownKeys = true; isLenient = true }

    @Test
    fun `decodes the playlists response`() {
        val body = """
            {
              "playlists": [
                {
                  "id": 3, "name": "Road trip",
                  "source": {
                    "id": 9, "type": "spotifyPlaylist", "name": "Road trip",
                    "imageUrl": "https://i.scdn.co/image/x", "url": "https://open.spotify.com/playlist/pl",
                    "autoSync": true, "lastSyncedAtUtc": "2026-09-26T10:00:00Z"
                  },
                  "songIds": [11, 10, 20], "addedSongIds": [20], "missingCount": 2,
                  "exportToLibrary": false, "exportedAtUtc": null,
                  "createdAtUtc": "2026-09-01T10:00:00Z", "updatedAtUtc": "2026-09-26T10:00:00Z"
                },
                { "id": 4, "name": "Mine", "source": null, "songIds": [], "addedSongIds": [], "missingCount": 0 }
              ],
              "canExport": true
            }
        """.trimIndent()

        val playlists = json.decodeFromString<PlaylistsResponse>(body).playlists

        assertEquals(2, playlists.size)
        assertEquals("spotifyPlaylist", playlists[0].source?.type)
        assertEquals(listOf(11, 10, 20), playlists[0].songIds)
        assertEquals(listOf(20), playlists[0].addedSongIds)
        assertEquals(2, playlists[0].missingCount)
        assertNull(playlists[1].source)
    }

    @Test
    fun `joins ids against the library in play order and skips what it does not hold`() {
        val a = song(1)
        val b = song(2)
        val dto = PlaylistDto(id = 1, name = "Mix", songIds = listOf(2, 99, 1), addedSongIds = listOf(1))

        val playlist = hydratePlaylists(listOf(dto), mapOf(1 to a, 2 to b)).single()

        assertEquals(listOf(2, 1), playlist.tracks.map { it.id })
        // The repository's own rows, so the like overlay reads the same objects everywhere.
        assertSame(b, playlist.tracks[0])
        assertEquals(setOf(1), playlist.addedIds)
    }

    @Test
    fun `describes a playlist as the web does`() {
        val spotify = PlaylistSourceDto(id = 1, type = "spotifyPlaylist")
        assertEquals("1 track", trackCountLabel(1))
        assertEquals("42 tracks", trackCountLabel(42))
        assertEquals("2 tracks", playlistSubtitle(null, 2))
        assertEquals("Spotify · 1 track", playlistSubtitle(spotify, 1))
        assertEquals("YouTube · Empty", playlistSubtitle(PlaylistSourceDto(id = 2, type = "youtube"), 0))
        assertEquals("Playlist", playlistKindLabel(null))
        assertEquals("Spotify Liked Songs", playlistKindLabel(PlaylistSourceDto(id = 3, type = "spotifyLiked")))
        assertEquals("Deezer playlist", playlistKindLabel(PlaylistSourceDto(id = 4, type = "deezer")))
        assertEquals("8 not in your library yet", missingLabel(spotify, 8))
        assertNull(missingLabel(spotify, 0))
        assertNull(missingLabel(null, 3))
    }

    @Test
    fun `offers the most recently changed playlists first`() {
        val older = PlaylistDto(id = 1, name = "Older", updatedAtUtc = "2026-01-01T00:00:00Z")
        val newer = PlaylistDto(id = 2, name = "Newer", updatedAtUtc = "2026-09-01T00:00:00Z")

        val ordered = recentFirst(hydratePlaylists(listOf(older, newer), emptyMap()))

        assertEquals(listOf(2, 1), ordered.map { it.id })
    }
}
