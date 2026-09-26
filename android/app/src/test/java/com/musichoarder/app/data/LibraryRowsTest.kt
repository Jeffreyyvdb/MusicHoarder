package com.musichoarder.app.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Test

/**
 * Which rows a picked-up playback session is resolved against: every `/songs` row, built or not —
 * the web's `songsStore.songsById`, which `adoptHere` resolves against. The Tracks list's narrower
 * base is not the library's whole answer: the account's session can be playing a track that is
 * matched but not built yet (it streams from the source), and a phone that could not pick that up
 * would say "This song isn't available here" where a browser tab carries on.
 *
 * Runs the repository's real fold over `/songs` rows, so the rule is pinned where it is made.
 */
class LibraryRowsTest {

    private fun row(id: Int, built: Boolean, enrichmentStatus: String? = null) = ApiSong(
        id = id,
        fileName = "$id.flac",
        title = "Track $id",
        artist = "An Artist",
        album = "An Album",
        destinationPath = if (built) "/library/An Artist/An Album/$id.flac" else null,
        originKind = "Downloaded",
        enrichmentStatus = enrichmentStatus?.let { kotlinx.serialization.json.JsonPrimitive(it) },
        isBuiltServer = built,
    )

    private val built = row(1, built = true)
    private val matchedNotBuilt = row(2, built = false, enrichmentStatus = "Matched")
    private val pending = row(3, built = false, enrichmentStatus = "Pending")

    private val state = LibraryRepository.fold(listOf(built, matchedNotBuilt, pending), emptyList())

    @Test
    fun `holds every row, while the Tracks list stays the built ones`() {
        assertEquals(listOf(1), state.trackListBase.map { it.id })
        assertEquals(setOf(1, 2, 3), state.songsById.keys)
    }

    @Test
    fun `a session playing an unbuilt track is picked up with its whole queue`() {
        val adoption = resolveAdoption(listOf(1, 2, 3), queueIndex = 1, songId = 2) { state.songsById[it] }

        assertNotNull(adoption)
        assertEquals(listOf(1, 2, 3), adoption!!.items.map { it.id })
        assertEquals(1, adoption.startIndex)
    }
}
