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
    fun `an optimistic heart moves the Favourites count`() {
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

    @Test
    fun `my music covers what you asked for, and a filled track only once you like it`() {
        val filled = song(id = 6, acquisitionIntent = "AlbumFill")
        val filledAndLiked = song(
            id = 7,
            acquisitionIntent = "AlbumFill",
            likedAtUtc = "2026-08-26T00:00:00Z",
        )

        assertEquals(true, isMyMusic(scanned, isLiked(scanned)))
        assertEquals(false, isMyMusic(filled, isLiked(filled)))
        assertEquals(true, isMyMusic(filledAndLiked, isLiked(filledAndLiked)))
        // The optimistic overlay promotes it before the next refetch lands.
        assertEquals(true, isMyMusic(filled, true))
    }

    @Test
    fun `labels and the member chip set match the web word for word`() {
        // track-list-view.svelte.ts: CHIP_LABELS and FRIEND_CHIP_KEYS. A member's list carries no
        // origin or Spotify data, so any other chip could only ever read 0 there.
        assertEquals("Favourites", CHIP_LABELS.getValue(ChipKey.MhLiked))
        assertEquals("Spotify liked", CHIP_LABELS.getValue(ChipKey.SpotifyLiked))
        assertEquals(listOf(ChipKey.MhLiked, ChipKey.Video, ChipKey.Lyrics), FRIEND_CHIP_KEYS)
        assertEquals(FRIEND_CHIP_KEYS, visibleChipKeys(isAdmin = false))
        assertEquals(CHIP_KEYS, visibleChipKeys(isAdmin = true))
    }

    @Test
    fun `a member is not offered the Spotify save date`() {
        assertEquals(false, SortKey.Spotify in visibleSortKeys(isAdmin = false))
        assertEquals(SortKey.entries.size - 1, visibleSortKeys(isAdmin = false).size)
        assertEquals(SortKey.entries, visibleSortKeys(isAdmin = true))
    }

    @Test
    fun `switching to a member lets go of what it cannot see, and the sort that came with it`() {
        val admin = LibraryUiState(
            chips = setOf(ChipKey.SpotifyLiked, ChipKey.Video),
            sortKey = SortKey.Spotify,
            sortAscending = false,
        )
        val member = admin.scopedTo(isAdmin = false)
        assertEquals(setOf(ChipKey.Video), member.chips)
        assertEquals(SortKey.Added, member.sortKey)
        assertEquals(false, member.sortAscending)
        // An admin keeps everything, and a member's own choices are left alone.
        assertEquals(admin, admin.scopedTo(isAdmin = true))
        val own = LibraryUiState(chips = setOf(ChipKey.MhLiked), sortKey = SortKey.Title, sortAscending = true)
        assertEquals(own, own.scopedTo(isAdmin = false))
        // A Spotify sort picked from the menu, with no chip behind it, goes back to the default too.
        val menuSort = LibraryUiState(sortKey = SortKey.Spotify, sortAscending = true).scopedTo(isAdmin = false)
        assertEquals(SortKey.Added to false, menuSort.sortKey to menuSort.sortAscending)
    }
}
