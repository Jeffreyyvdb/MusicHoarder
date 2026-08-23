package com.musichoarder.app.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** Pins artist grouping to `buildArtistGroups` and `applyBrowseFilter` on the web. */
class ArtistGroupingTest {

    @Test
    fun `a multi-artist track is listed under each credited artist`() {
        val track = song(
            id = 1,
            artist = "Metro Boomin",
            albumArtist = "Metro Boomin",
            artists = "21 Savage; Travis Scott; Metro Boomin",
        )
        val groups = buildArtistGroups(listOf(track), primaryOnly = false)
        assertEquals(listOf("21 Savage", "Metro Boomin", "Travis Scott"), groups.map { it.label })
    }

    @Test
    fun `primary drops artists who never lead, but keeps their features on a lead's card`() {
        val leadTrack = song(
            id = 1,
            albumArtist = "Metro Boomin",
            artists = "Metro Boomin; Travis Scott",
        )
        val guestOnly = song(
            id = 2,
            albumArtist = "Metro Boomin",
            artists = "Metro Boomin; The Weeknd",
            album = "Another Album",
            destinationPath = "/library/Metro Boomin/Another Album/2.flac",
        )
        val tracks = listOf(leadTrack, guestOnly)

        assertEquals(listOf("Metro Boomin"), buildArtistGroups(tracks, primaryOnly = true).map { it.label })
        assertEquals(
            listOf("Metro Boomin", "The Weeknd", "Travis Scott"),
            buildArtistGroups(tracks, primaryOnly = false).map { it.label },
        )
        // The lead's own card still counts both tracks, features included.
        assertEquals(2, buildArtistGroups(tracks, primaryOnly = true).single().trackCount)
    }

    @Test
    fun `album count is distinct album names, not destination folders`() {
        // The same album split across two folders is one album to an artist's card, even though the
        // grid it drills into shows the merge. Worth pinning: the two numbers can differ legitimately.
        val tracks = listOf(
            song(id = 1, artist = "Wally", album = "TUSI", destinationPath = "/l/Wally/TUSI/1.flac"),
            song(id = 2, artist = "Wally", album = "TUSI", destinationPath = "/l/Wally/TUSI (2026)/2.flac"),
        )
        assertEquals(1, buildArtistGroups(tracks, primaryOnly = true).single().albumCount)
    }

    @Test
    fun `names are ordered the way the browser orders them, not by codepoint`() {
        // Kotlin's own compareTo files every lowercase name after every uppercase one and puts
        // "Olafur" past "Z". This is exactly the class of silent divergence AlbumTintTest exists for.
        val tracks = listOf("Oasis", "oasis fan club", "Ólafur Arnalds", "Zebra").mapIndexed { i, name ->
            song(id = i + 1, artist = name, albumArtist = name, destinationPath = "/l/$name/a/$i.flac")
        }
        assertEquals(
            listOf("Oasis", "oasis fan club", "Ólafur Arnalds", "Zebra"),
            buildArtistGroups(tracks, primaryOnly = true).map { it.label },
        )
    }

    @Test
    fun `the A-Z bucket folds non-letters into hash`() {
        assertEquals("K", artistInitial("Kid Cudi"))
        // The web tests the uppercased initial against /[A-Z]/, so an accent falls to the # bucket
        // too. Unlike the web, the phone renders a # button, so those names stay reachable.
        assertEquals("#", artistInitial("Ólafur Arnalds"))
        assertEquals("#", artistInitial("21 Savage"))
        assertEquals("#", artistInitial("\$NOT"))
        assertEquals("#", artistInitial("   "))
    }

    @Test
    fun `the drilldown matches a lead artist or any discrete credit`() {
        val track = song(
            id = 1,
            albumArtist = "Metro Boomin",
            artists = "Metro Boomin; Travis Scott",
        )
        assertTrue(matchesArtist(track, "metro boomin"))
        assertTrue(matchesArtist(track, "Travis Scott"))
        assertFalse(matchesArtist(track, "Kid Cudi"))
    }
}
