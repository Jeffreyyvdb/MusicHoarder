package com.musichoarder.app.data

import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Pins the wire contract with `SharesEndpoints.GetSharePayload` and the [toTrack] adaptation. */
class ShareDtoTest {

    private val json = Json { ignoreUnknownKeys = true; isLenient = true }

    // A realistic body: camelCase keys, nullable fields present-as-null, plus an unknown key the
    // decoder must ignore (the server is free to grow the payload).
    private val payloadJson = """
        {
          "scope": "Album",
          "sharedSongId": 42,
          "album": { "title": "Better Dayz", "artist": "2Pac", "year": 2002 },
          "tracks": [
            {
              "id": 42, "title": "Thugz Mansion", "artist": "2Pac", "trackNumber": 4,
              "discNumber": 1, "durationMs": 245000, "hasCoverArt": true,
              "hasSyncedLyrics": true, "hasPlainLyrics": false, "isInstrumental": false,
              "hasVideo": true, "videoOffsetMs": 120, "videoDurationSeconds": 250
            },
            {
              "id": 43, "title": "", "artist": null, "trackNumber": null, "discNumber": null,
              "durationMs": null, "hasCoverArt": false, "hasSyncedLyrics": false,
              "hasPlainLyrics": false, "isInstrumental": true, "hasVideo": false,
              "videoOffsetMs": null, "videoDurationSeconds": null, "someFutureField": 7
            }
          ]
        }
    """.trimIndent()

    @Test
    fun `decodes the share payload`() {
        val payload = json.decodeFromString<SharePayload>(payloadJson)
        assertEquals("Album", payload.scope)
        assertEquals(42, payload.sharedSongId)
        assertEquals("Better Dayz", payload.album.title)
        assertEquals(2, payload.tracks.size)
        assertEquals(245000L, payload.tracks[0].durationMs)
        assertTrue(payload.tracks[0].hasVideo)
        assertTrue(payload.tracks[1].isInstrumental)
    }

    @Test
    fun `toTrack carries the share URLs and album identity`() {
        val payload = json.decodeFromString<SharePayload>(payloadJson)
        val track = payload.tracks[0].toTrack(
            album = payload.album,
            streamUrl = "https://host/api/mh/api/share/tok/songs/42/stream",
            artworkUrl = "https://host/api/mh/api/share/tok/songs/42/cover?size=640",
        )
        assertEquals(42, track.id)
        assertEquals("Thugz Mansion", track.title)
        assertEquals("2Pac", track.artist)
        assertEquals("Better Dayz", track.album)
        assertEquals("2Pac", track.albumArtist)
        assertEquals(2002, track.year)
        assertEquals(245000L, track.durationMs)
        assertEquals(245, track.durationSeconds)
        assertTrue(track.hasCover)
        assertTrue(track.hasLyrics)
        assertEquals("https://host/api/mh/api/share/tok/songs/42/stream", track.streamUrl)
        assertEquals("https://host/api/mh/api/share/tok/songs/42/cover?size=640", track.artworkUrl)
        // Foreign ids never feed owner-ish state.
        assertNull(track.likedAtUtc)
        assertEquals(0, track.playCount)
        assertFalse(track.needsReview)
    }

    @Test
    fun `toTrack falls back on blank title and missing artists`() {
        val payload = json.decodeFromString<SharePayload>(payloadJson)
        val track = payload.tracks[1].toTrack(payload.album, streamUrl = "s", artworkUrl = null)
        assertEquals("Untitled", track.title)
        // No track credit: the album artist stands in before "Unknown artist".
        assertEquals("2Pac", track.artist)
        assertEquals(0, track.durationSeconds)
        assertFalse(track.hasLyrics)
        assertNull(track.artworkUrl)
    }

    @Test
    fun `library tracks default to no URL overrides`() {
        val track = ApiSong(id = 1, fileName = "x.mp3").toTrack()
        assertNull(track.streamUrl)
        assertNull(track.artworkUrl)
    }

    @Test
    fun `decodes the invite peek and access token`() {
        val peek = json.decodeFromString<InvitePeek>("""{ "inviterName": "Jeffrey", "email": "friend@example.com" }""")
        assertEquals("Jeffrey", peek.inviterName)
        assertEquals("friend@example.com", peek.email)

        // accept-token answers the same shape as /api/auth/token, decoded with the shared DTO.
        val token = json.decodeFromString<AccessTokenResponse>(
            """{ "accessToken": "abc", "tokenType": "Bearer", "expiresAtUtc": "2026-09-24T00:00:00Z" }"""
        )
        assertEquals("abc", token.accessToken)
    }
}
