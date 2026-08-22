package com.musichoarder.app.data

import org.junit.Assert.assertEquals
import org.junit.Test

/** Pins the chip and sort semantics to `frontend/src/lib/track-list-view.svelte.ts`. */
class TrackListViewTest {

    private val liked = song(id = 1, likedAtUtc = "2024-01-01T00:00:00Z")
    private val scanned = song(id = 2, originKind = "Scanned")
    private val fromUrl = song(id = 3, originKind = "Downloaded", originSource = "DirectUrl")
    private val leaked = song(id = 4, releaseClassification = "LikelyUnreleased")
    private val withVideo = song(id = 5, hasMusicVideo = true)
    private val all = listOf(liked, scanned, fromUrl, leaked, withVideo)

    private val likes = emptyMap<Int, String?>()
    private val isLiked: (Track) -> Boolean = { likedNow(likes, it) }

    @Test
    fun `chips compose with AND`() {
        assertEquals(
            listOf(4),
            applyChips(all, setOf(ChipKey.Unreleased), isLiked).map { it.id },
        )
        // One is scanned, the other downloaded, so together they can never match.
        assertEquals(
            emptyList<Int>(),
            applyChips(all, setOf(ChipKey.Local, ChipKey.Added), isLiked).map { it.id },
        )
    }

    @Test
    fun `both tiers of unreleased are covered`() {
        val tracks = listOf(
            song(id = 1, releaseClassification = "Unreleased"),
            song(id = 2, releaseClassification = "LikelyUnreleased"),
            song(id = 3, releaseClassification = "Released"),
            song(id = 4, releaseClassification = null),
        )
        assertEquals(
            listOf(1, 2),
            applyChips(tracks, setOf(ChipKey.Unreleased), isLiked).map { it.id },
        )
    }

    @Test
    fun `a chip's count is what pressing it would leave`() {
        // Measured against the search and every OTHER active chip - which is what makes a dead-end
        // combination read 0 before you press it rather than after.
        val counts = chipCounts(all, setOf(ChipKey.Local), isLiked)
        assertEquals(1, counts.getValue(ChipKey.Local))
        assertEquals(0, counts.getValue(ChipKey.Added))
        assertEquals(0, counts.getValue(ChipKey.Unreleased))
    }

    @Test
    fun `an active chip's count is the current result count`() {
        val counts = chipCounts(all, setOf(ChipKey.Unreleased), isLiked)
        assertEquals(1, counts.getValue(ChipKey.Unreleased))
    }

    @Test
    fun `an optimistic heart moves the MusicHoarder Liked count`() {
        val optimistic = mapOf(scanned.id to "2026-08-22T00:00:00Z")
        val counts = chipCounts(all, emptySet()) { likedNow(optimistic, it) }
        assertEquals(2, counts.getValue(ChipKey.MhLiked))
    }

    @Test
    fun `pressing Spotify Liked switches the sort, and releasing it restores the default`() {
        assertEquals(
            SortKey.Spotify to false,
            sortForChipChange(emptySet(), setOf(ChipKey.SpotifyLiked), SortKey.Added, false),
        )
        assertEquals(
            SortKey.Added to false,
            sortForChipChange(setOf(ChipKey.SpotifyLiked), emptySet(), SortKey.Spotify, false),
        )
    }

    @Test
    fun `an unrelated chip leaves a chosen sort alone`() {
        // Only a transition of the Spotify chip touches the sort.
        assertEquals(
            SortKey.Title to true,
            sortForChipChange(setOf(ChipKey.Local), setOf(ChipKey.Local, ChipKey.Video), SortKey.Title, true),
        )
    }

    @Test
    fun `text keys read ascending by default, everything else newest first`() {
        assertEquals(true, defaultAscending(SortKey.Title))
        assertEquals(true, defaultAscending(SortKey.Artist))
        assertEquals(true, defaultAscending(SortKey.Album))
        assertEquals(false, defaultAscending(SortKey.Added))
        assertEquals(false, defaultAscending(SortKey.Duration))
    }

    @Test
    fun `search matches title, lead artist and album`() {
        val tracks = listOf(
            song(id = 1, title = "Mr. Rager", artist = "Kid Cudi", albumArtist = "Kid Cudi"),
            song(id = 2, title = "Westside", artist = "Kocky Ka", albumArtist = "Kocky Ka", album = "Rager Tapes"),
            song(id = 3, title = "TUSI", artist = "Wally", albumArtist = "Wally"),
        )
        assertEquals(listOf(1, 2), searchTracks(tracks, "rager").map { it.id })
        assertEquals(listOf(3), searchTracks(tracks, "wal").map { it.id })
        assertEquals(3, searchTracks(tracks, "   ").size)
    }
}
