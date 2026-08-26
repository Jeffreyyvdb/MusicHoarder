package com.musichoarder.app.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Test

/**
 * What is left of album grouping on this side.
 *
 * The grouping itself — folder keys, the name merge, the year election, the added-date rule — moved
 * to `GET /api/albums` and is pinned by `AlbumProjectionTests` in the API suite. It used to live
 * here, as a hand-written port of `buildAlbumsFromSongs`, alongside the web's own copy; that is how
 * one added-date rule came to need fixing twice and how the phone and the browser came to disagree
 * about which track names a card. What stays here is the join and the grid's own ordering.
 */
class AlbumGroupingTest {

    @Test
    fun `hydration resolves track ids to the very rows the library holds`() {
        // Identity, not equality: likes and play counts are overlays keyed on these rows, and an
        // album carrying its own copies would show a stale heart until the next refresh.
        val one = song(id = 1, title = "Gorgeous")
        val two = song(id = 2, title = "Power")

        val album = hydrateAlbums(listOf(card(trackIds = listOf(2, 1))), mapOf(1 to one, 2 to two))
            .single()

        assertEquals(listOf(2, 1), album.tracks.map { it.id })
        assertSame(two, album.tracks.first())
    }

    @Test
    fun `a track the library does not have is dropped rather than left as a hole`() {
        // The two fetches can straddle a library change; the next refresh reconciles.
        val album = hydrateAlbums(listOf(card(trackIds = listOf(1, 99))), mapOf(1 to song(id = 1)))
            .single()

        assertEquals(listOf(1), album.tracks.map { it.id })
    }

    @Test
    fun `the cover comes from the server, and falls back to a local scan`() {
        val withArt = song(id = 1, hasCoverArt = true)
        val tracks = mapOf(1 to withArt)

        assertEquals(1, hydrateAlbums(listOf(card(trackIds = listOf(1), coverSongId = 1)), tracks).single().coverTrackId)
        // An older server does not name one; the joined tracks still can.
        assertEquals(1, hydrateAlbums(listOf(card(trackIds = listOf(1))), tracks).single().coverTrackId)
        assertNull(hydrateAlbums(listOf(card()), emptyMap()).single().coverTrackId)
    }

    @Test
    fun `a card with no folders of its own is still addressable by its key`() {
        assertEquals(listOf("a::b"), hydrateAlbums(listOf(card(key = "a::b")), emptyMap()).single().folderKeys)
    }

    @Test
    fun `every order falls back to artist then title, so ties stay alphabetical`() {
        // None of these has a play count, a year, or an added date, so only the fallback can order
        // them. Without it they would come out in whatever order the server happened to send.
        val albums = listOf(
            summary(artist = "Zeta", name = "Charlie"),
            summary(artist = "Alpha", name = "Delta"),
            summary(artist = "Alpha", name = "Beta"),
        )

        // Title is excluded: it is the one key whose own comparator has something to say here.
        for (key in AlbumSortKey.entries - AlbumSortKey.Title) {
            assertEquals(
                "sorted by $key",
                listOf("Beta", "Delta", "Charlie"),
                sortAlbums(albums, key).map { it.name },
            )
        }
        assertEquals(listOf("Beta", "Charlie", "Delta"), sortAlbums(albums, AlbumSortKey.Title).map { it.name })
    }

    @Test
    fun `names are collated, not compared by codepoint`() {
        // Kotlin's natural String order would file every lowercase name after every uppercase one,
        // and "Ólafur" past "Z". The server orders the same way, using the invariant culture.
        val albums = listOf(
            summary(artist = "Zola", name = "Z"),
            summary(artist = "Ólafur", name = "O"),
            summary(artist = "aphex", name = "A"),
        )

        assertEquals(listOf("aphex", "Ólafur", "Zola"), sortAlbums(albums, AlbumSortKey.Artist).map { it.artist })
    }

    @Test
    fun `most played sorts on the play count the server summed`() {
        val albums = listOf(summary(name = "Quiet", playCount = 1), summary(name = "Loud", playCount = 9))

        assertEquals(listOf("Loud", "Quiet"), sortAlbums(albums, AlbumSortKey.Played).map { it.name })
    }

    @Test
    fun `recently added sorts on the album date the server decided`() {
        val albums = listOf(
            summary(name = "Old", addedAtUtc = "2019-01-01T00:00:00Z"),
            summary(name = "New", addedAtUtc = "2026-08-01T00:00:00Z"),
        )

        assertEquals(listOf("New", "Old"), sortAlbums(albums, AlbumSortKey.Recent).map { it.name })
    }

    private fun card(
        key: String = "an artist::an album",
        coverSongId: Int? = null,
        trackIds: List<Int> = emptyList(),
    ) = AlbumSummaryDto(
        key = key,
        folderKeys = emptyList(),
        nameKey = key,
        title = "An Album",
        artist = "An Artist",
        trackIds = trackIds,
        coverSongId = coverSongId,
    )

    /** An already-joined card, for the ordering tests — they never look at the tracks. */
    private fun summary(
        artist: String = "An Artist",
        name: String = "An Album",
        year: Int? = null,
        playCount: Int = 0,
        addedAtUtc: String? = null,
    ) = hydrateAlbums(
        listOf(
            AlbumSummaryDto(
                key = "${artist.lowercase()}::${name.lowercase()}",
                nameKey = "${artist.lowercase()}::${name.lowercase()}",
                title = name,
                artist = artist,
                year = year,
                playCount = playCount,
                addedAtUtc = addedAtUtc,
            ),
        ),
        emptyMap(),
    ).single()
}
