package com.musichoarder.app.data

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * Pins what the Tracks tab covers. Album completion's tracks belong to the album views, not to the
 * flat list of the music you chose — the same rule as `trackListBase` in
 * `frontend/src/lib/components/v2/LibraryV2.svelte`.
 */
class LibraryFoldTest {

    private val chosen = song(
        id = 1,
        title = "Mob Ties",
        album = "Scorpion",
        destinationPath = "/library/Drake/Scorpion/1.flac",
    )
    private val filled = song(
        id = 2,
        title = "Nonstop",
        album = "Scorpion",
        acquisitionIntent = "AlbumFill",
        destinationPath = "/library/Drake/Scorpion/2.flac",
    )
    private val kept = song(
        id = 3,
        title = "Survival",
        album = "Scorpion",
        acquisitionIntent = "AlbumFill",
        likedAtUtc = "2026-08-26T00:00:00Z",
        destinationPath = "/library/Drake/Scorpion/3.flac",
    )

    private val all = listOf(chosen, filled, kept)
    private val state = LibraryState(
        builtTracks = all,
        trackListBase = all,
        albums = mergeAlbumsByName(buildAlbums(all)),
    )

    private fun fold(likes: Map<Int, String?> = emptyMap()): LibraryContent =
        foldLibrary(state, LibraryUiState(), likes, emptyMap())

    @Test
    fun `the tracks list leaves out album fill, and keeps a filled track you liked`() {
        assertEquals(listOf(1, 3), fold().tracks.map { it.id }.sorted())
        assertEquals(2, fold().trackListCount)
    }

    @Test
    fun `an optimistic heart promotes a filled track without waiting for a refetch`() {
        val liked = fold(mapOf(2 to "2026-08-26T00:00:00Z"))
        assertEquals(listOf(1, 2, 3), liked.tracks.map { it.id }.sorted())
    }

    @Test
    fun `the album still holds every track, so a filled record looks complete`() {
        assertEquals(3, fold().albums.single().trackCount)
    }
}
