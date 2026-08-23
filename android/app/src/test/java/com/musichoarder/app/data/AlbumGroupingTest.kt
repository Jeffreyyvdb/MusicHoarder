package com.musichoarder.app.data

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * Pins album grouping to `buildAlbumsFromSongs` / `mergeAlbumsByName` / `sortAlbums` in
 * `frontend/src/lib/api-client.ts`.
 *
 * This is the part of the port that changes what the user sees most obviously: the phone used to
 * group on `"$albumArtist $album"`, which produces a different album count from the browser.
 */
class AlbumGroupingTest {

    private fun split(id: Int, folder: String, year: Int? = null, tracks: Int = 1) =
        (1..tracks).map {
            song(
                id = id * 100 + it,
                album = "Ye",
                artist = "Kanye West",
                year = year,
                trackNumber = it,
                destinationPath = "$folder/$it.flac",
            )
        }

    @Test
    fun `built tracks group by destination folder, not by tags`() {
        val tracks = split(1, "/library/Kanye West/Ye (2018)") + split(2, "/library/Kanye West/Ye")
        assertEquals(2, buildAlbums(tracks).size)
    }

    @Test
    fun `the name merge folds those folders back into one card`() {
        val tracks = split(1, "/library/Kanye West/Ye (2018)") + split(2, "/library/Kanye West/Ye")
        val merged = mergeAlbumsByName(buildAlbums(tracks))
        assertEquals(1, merged.size)
        assertEquals(2, merged.single().trackCount)
    }

    @Test
    fun `the largest folder becomes the representative`() {
        val small = split(1, "/library/Kanye West/Ye (bootleg)", tracks = 1)
        val large = split(2, "/library/Kanye West/Ye", tracks = 3)
        val merged = mergeAlbumsByName(buildAlbums(small + large)).single()
        assertEquals("/library/Kanye West/Ye", merged.key)
        // Every folder is still addressable, so a drilldown into the loser resolves.
        assertEquals(
            listOf("/library/Kanye West/Ye", "/library/Kanye West/Ye (bootleg)"),
            merged.folderKeys,
        )
    }

    @Test
    fun `a tie is broken on the key, so the choice is stable across refetches`() {
        val a = split(1, "/library/Kanye West/Ye A", tracks = 2)
        val b = split(2, "/library/Kanye West/Ye B", tracks = 2)
        assertEquals("/library/Kanye West/Ye A", mergeAlbumsByName(buildAlbums(b + a)).single().key)
        assertEquals("/library/Kanye West/Ye A", mergeAlbumsByName(buildAlbums(a + b)).single().key)
    }

    @Test
    fun `the album year is the earliest its tracks agree on`() {
        // A deluxe re-issue's tracks carry the reissue year; the album is still the year it came out.
        val tracks = split(1, "/library/Kanye West/Ye", year = 2018, tracks = 1) +
            split(2, "/library/Kanye West/Ye (Deluxe)", year = 2021, tracks = 1)
        assertEquals(2018, mergeAlbumsByName(buildAlbums(tracks)).single().year)
    }

    @Test
    fun `a song with no destination path falls back to its name key`() {
        val track = song(id = 1, artist = "Wally", album = "TUSI", destinationPath = null)
        assertEquals("wally::tusi", buildAlbums(listOf(track)).single().key)
    }

    @Test
    fun `every order falls back to artist then title, so ties stay alphabetical`() {
        // None of these has a play count, a year, or an added date, so only the fallback can order
        // them. Without it they would come out in whatever order the grouping map happened to build.
        val tracks = listOf(
            song(id = 1, artist = "Zebra", album = "Beta", destinationPath = "/l/z/b/1.flac"),
            song(id = 2, artist = "Alpha", album = "Delta", destinationPath = "/l/a/d/2.flac"),
            song(id = 3, artist = "Alpha", album = "Charlie", destinationPath = "/l/a/c/3.flac"),
        )
        val albums = buildAlbums(tracks)
        // Title is excluded: it is the one key whose own comparator has something to say here.
        for (key in AlbumSortKey.entries - AlbumSortKey.Title) {
            assertEquals(
                "sorted by $key",
                listOf("Charlie", "Delta", "Beta"),
                sortAlbums(albums, key).map { it.name },
            )
        }
        assertEquals(
            listOf("Beta", "Charlie", "Delta"),
            sortAlbums(albums, AlbumSortKey.Title).map { it.name },
        )
    }

    @Test
    fun `most played sorts on the summed play count`() {
        val quiet = song(id = 1, album = "Quiet", playCount = 1, destinationPath = "/l/a/q/1.flac")
        val loud = listOf(
            song(id = 2, album = "Loud", playCount = 3, destinationPath = "/l/a/l/2.flac"),
            song(id = 3, album = "Loud", playCount = 4, destinationPath = "/l/a/l/3.flac"),
        )
        val albums = buildAlbums(loud + quiet)
        assertEquals(
            listOf("Loud", "Quiet"),
            sortAlbums(albums, AlbumSortKey.Played).map { it.name },
        )
    }
}
