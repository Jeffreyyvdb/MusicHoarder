package com.musichoarder.app.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** Pins the Overview shelves to `LibraryOverviewV2.svelte`. */
class OverviewSectionsTest {

    @Test
    fun `the greeting changes on the hours the web changes it on`() {
        assertEquals("Night owl session", greetingForHour(0))
        assertEquals("Night owl session", greetingForHour(5))
        assertEquals("Good morning", greetingForHour(6))
        assertEquals("Good morning", greetingForHour(11))
        assertEquals("Good afternoon", greetingForHour(12))
        assertEquals("Good afternoon", greetingForHour(17))
        assertEquals("Good evening", greetingForHour(18))
        assertEquals("Good evening", greetingForHour(23))
    }

    @Test
    fun `the seeded hash matches JavaScript as an unsigned 32-bit value`() {
        // JS: h = 5381; for (c of s) h = ((h << 5) + h + c.charCodeAt(0)) >>> 0
        // Kotlin Int arithmetic wraps the same way; the trap is reading the result back as signed.
        assertEquals(177_670L, djb2("a"))
        assertEquals(193_485_963L, djb2("abc"))
        // Long enough to overflow 32 bits many times over, which is where a signed read diverges.
        assertTrue(djb2("a very long key indeed, long enough to wrap") in 0L..0xFFFF_FFFFL)
    }

    @Test
    fun `the same seed gives the same order, a different seed does not`() {
        val items = (1..20).map { "item-$it" }
        assertEquals(seededOrder(items, "abc") { it }, seededOrder(items, "abc") { it })
        assertTrue(seededOrder(items, "abc") { it } != seededOrder(items, "xyz") { it })
        // A permutation, not a filter.
        assertEquals(items.toSet(), seededOrder(items, "abc") { it }.toSet())
        // And it is a shuffle rather than a sort: keys sharing a long prefix must not come back in
        // input order, which is what the web's suffix-seeded djb2 does.
        assertTrue(seededOrder(items, "abc") { it } != items)
    }

    @Test
    fun `the order does not depend on the order the items arrived in`() {
        val items = (1..20).map { "item-$it" }
        assertEquals(
            seededOrder(items, "abc") { it },
            seededOrder(items.reversed(), "abc") { it },
        )
    }

    @Test
    fun `never-played shelves exclude an album with any play at all`() {
        val untouched = album("Untouched", song(id = 1, album = "Untouched", destinationPath = "/l/a/u/1.flac"))
        val partly = album(
            "Partly",
            song(id = 2, album = "Partly", playCount = 1, destinationPath = "/l/a/p/2.flac"),
            song(id = 3, album = "Partly", destinationPath = "/l/a/p/3.flac"),
        )
        val sections = build(listOf(untouched, partly))
        assertEquals(listOf("Untouched"), sections.discoverAlbums.map { it.name })
    }

    @Test
    fun `New to you needs an unliked album-fill track and no plays anywhere on the album`() {
        val filled = album(
            "Filled",
            song(id = 1, album = "Filled", acquisitionIntent = "AlbumFill", destinationPath = "/l/a/f/1.flac"),
        )
        val filledButLiked = album(
            "Liked",
            song(
                id = 2,
                album = "Liked",
                acquisitionIntent = "AlbumFill",
                likedAtUtc = "2024-01-01T00:00:00Z",
                destinationPath = "/l/a/l/2.flac",
            ),
        )
        val filledButPlayed = album(
            "Played",
            song(
                id = 3,
                album = "Played",
                acquisitionIntent = "AlbumFill",
                playCount = 2,
                destinationPath = "/l/a/pl/3.flac",
            ),
        )
        val asked = album(
            "Asked",
            song(id = 4, album = "Asked", destinationPath = "/l/a/as/4.flac"),
        )

        val sections = build(listOf(filled, filledButLiked, filledButPlayed, asked))
        assertEquals(listOf("Filled"), sections.newToYouAlbums.map { it.name })
    }

    @Test
    fun `Last played excludes albums nothing has ever played`() {
        val played = album(
            "Played",
            song(id = 1, album = "Played", lastPlayedAtUtc = "2026-08-01T00:00:00Z", destinationPath = "/l/a/p/1.flac"),
        )
        val never = album("Never", song(id = 2, album = "Never", destinationPath = "/l/a/n/2.flac"))
        assertEquals(listOf("Played"), build(listOf(played, never)).lastPlayedAlbums.map { it.name })
    }

    @Test
    fun `favourites are the ten most recent hearts, newest first`() {
        val tracks = (1..12).map {
            song(id = it, likedAtUtc = "2024-01-%02dT00:00:00Z".format(it))
        } + song(id = 99, likedAtUtc = null)
        val sections = buildOverviewSections(
            tracks = tracks,
            albums = emptyList(),
            artists = emptyList(),
            seed = "seed",
            likedAtMsOf = { parseIsoUtcMillis(it.likedAtUtc) },
            playCountOf = { it.playCount },
            lastPlayedAtMsOf = { it.lastPlayedAtMs },
        )
        assertEquals(10, sections.favouriteTracks.size)
        assertEquals(listOf(12, 11, 10), sections.favouriteTracks.take(3).map { it.id })
    }

    private fun album(name: String, vararg tracks: Track): Album =
        albumOf(tracks.toList(), name = name)

    private fun build(albums: List<Album>): OverviewSections = buildOverviewSections(
        tracks = albums.flatMap { it.tracks },
        albums = albums,
        artists = emptyList(),
        seed = "seed",
        likedAtMsOf = { it.likedAtMs },
        playCountOf = { it.playCount },
        lastPlayedAtMsOf = { it.lastPlayedAtMs },
    )
}
