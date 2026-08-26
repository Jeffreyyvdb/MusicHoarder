package com.musichoarder.app.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * Pins where the player's `artist · album` line goes, against the same rules the web's track panel
 * builds its two hrefs from (`TrackPanel.svelte`): the artist half links to the LEAD artist even
 * when the line shows the full credit, and the album half to the destination folder its card is
 * keyed on.
 */
class NowPlayingLinksTest {

    private val solo = song(
        id = 1,
        title = "Mob Ties",
        artist = "Drake",
        album = "Scorpion",
        destinationPath = "/library/Drake/Scorpion/1.flac",
    )
    private val featured = song(
        id = 2,
        title = "Betrayal",
        artist = "Trippie Redd, Drake",
        artists = "Trippie Redd; Drake",
        albumArtist = "Trippie Redd",
        album = "Trip at Knight",
        destinationPath = "/library/Trippie Redd/Trip at Knight/2.flac",
    )
    private val unbuilt = song(
        id = 3,
        title = "Something scanned",
        album = "Loose files",
        destinationPath = null,
    )

    private val built = listOf(solo, featured)

    private val state = LibraryState(
        builtTracks = built,
        // Wider than `builtTracks`, exactly as the repository builds it: the player resolves rows
        // from here, so an unbuilt track IS reachable and has to be turned away on its own merit.
        trackListBase = built + unbuilt,
        albums = mergeAlbumsByName(buildAlbums(built)),
    )

    @Test
    fun `links to the lead artist, not to the credit the line shows`() {
        val links = resolveNowPlayingLinks(state, featured.id)
        assertEquals("Trippie Redd", links?.artist)
    }

    @Test
    fun `links to the album's destination folder, so the drilldown resolves`() {
        val links = resolveNowPlayingLinks(state, solo.id)
        assertEquals("/library/Drake/Scorpion", links?.albumKey)
        assertEquals("Scorpion", resolveAlbum(state.albums, links!!.albumKey)?.name)
    }

    @Test
    fun `an unbuilt track has no cards to link to`() {
        assertNull(resolveNowPlayingLinks(state, unbuilt.id))
    }

    @Test
    fun `nothing playing, and an id this library never heard of, link nowhere`() {
        assertNull(resolveNowPlayingLinks(state, null))
        assertNull(resolveNowPlayingLinks(state, 999))
    }

    @Test
    fun `a folder that lost the name merge still lands on the surviving card`() {
        // The same album built into two folders — a re-issue's tracks carry a different year. The
        // merge elects the bigger folder and drops the other's key, and the player may still be
        // holding the loser: it has to resolve to the card that survived, not to nothing.
        val reissue = song(
            id = 4,
            artist = "Drake",
            album = "Scorpion",
            destinationPath = "/library/Drake/Scorpion (2018)/4.flac",
        )
        val all = built + reissue
        val merged = LibraryState(
            builtTracks = all,
            trackListBase = all,
            albums = mergeAlbumsByName(buildAlbums(all)),
        )
        val links = resolveNowPlayingLinks(merged, reissue.id)
        assertEquals("/library/Drake/Scorpion", links?.albumKey)
    }
}
