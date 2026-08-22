package com.musichoarder.app.data

/**
 * The filter chips and sort orders the Tracks list offers. Port of
 * `frontend/src/lib/track-list-view.svelte.ts`.
 *
 * Two axes are deliberately kept apart even though they read alike: [Local] is *where the file came
 * from* (a scan of your source share), while [Unreleased] is *what the recording is* (a
 * tracker-confirmed leak, snippet, demo or stem). A local file is very often a released one.
 */
enum class ChipKey { SpotifyLiked, MhLiked, Local, Added, Video, Lyrics, Unreleased }

/** Display order, which is also the order the chip row renders in. */
val CHIP_KEYS: List<ChipKey> = listOf(
    ChipKey.SpotifyLiked,
    ChipKey.MhLiked,
    ChipKey.Local,
    ChipKey.Added,
    ChipKey.Video,
    ChipKey.Lyrics,
    ChipKey.Unreleased,
)

val CHIP_LABELS: Map<ChipKey, String> = mapOf(
    ChipKey.SpotifyLiked to "Spotify Liked",
    ChipKey.MhLiked to "MusicHoarder Liked",
    ChipKey.Local to "Local files",
    ChipKey.Added to "Manually added",
    ChipKey.Video to "Has video",
    ChipKey.Lyrics to "With lyrics",
    ChipKey.Unreleased to "Unreleased",
)

/**
 * Whether a chip matches. [likedOf] is passed in rather than read off the track so an optimistic
 * heart tap is reflected without rebuilding every [Track] — see `LibraryRepository`.
 */
fun chipMatches(key: ChipKey, track: Track, isLiked: Boolean): Boolean = when (key) {
    ChipKey.SpotifyLiked -> track.isSpotifyLiked
    ChipKey.MhLiked -> isLiked
    ChipKey.Local -> track.isLocalFile
    ChipKey.Added -> track.isAddedByLink
    ChipKey.Video -> track.hasVideo
    ChipKey.Lyrics -> track.hasLyrics
    ChipKey.Unreleased -> track.isUnreleased
}

/** Every active chip must match. */
fun applyChips(tracks: List<Track>, chips: Set<ChipKey>, isLiked: (Track) -> Boolean): List<Track> {
    if (chips.isEmpty()) return tracks
    return tracks.filter { track -> chips.all { chipMatches(it, track, isLiked(track)) } }
}

/**
 * How many rows each chip would leave, measured against the list already narrowed by the search box
 * and every *other* active chip.
 *
 * That framing is what makes plain AND safe to expose: an inactive chip's count is exactly what you
 * get by pressing it, and a combination with no overlap (Local files + Manually added — one is
 * scanned, the other downloaded) reads 0 *before* you press it rather than after. An active chip's
 * count is the current result count, since excluding itself and re-applying is a no-op.
 */
fun chipCounts(
    searched: List<Track>,
    chips: Set<ChipKey>,
    isLiked: (Track) -> Boolean,
): Map<ChipKey, Int> {
    val counts = HashMap<ChipKey, Int>(CHIP_KEYS.size)
    for (key in CHIP_KEYS) counts[key] = 0
    for (track in searched) {
        val liked = isLiked(track)
        for (key in CHIP_KEYS) {
            // "Every other active chip" — the chip being measured excludes only itself.
            val othersMatch = chips.all { it == key || chipMatches(it, track, liked) }
            if (othersMatch && chipMatches(key, track, liked)) counts[key] = counts.getValue(key) + 1
        }
    }
    return counts
}

/**
 * The orders the Tracks list offers.
 *
 * The web reaches these through sortable column headers; a phone has no room for them, so this is
 * the set that still means something at 411dp — `size` and `match` belong to the desktop-only
 * columns and are left out.
 */
enum class SortKey { Added, Liked, Spotify, Title, Artist, Album, Year, Duration }

val SORT_LABELS: Map<SortKey, String> = mapOf(
    SortKey.Added to "Date added",
    SortKey.Liked to "Date liked",
    SortKey.Spotify to "Spotify save date",
    SortKey.Title to "Title",
    SortKey.Artist to "Artist",
    SortKey.Album to "Album",
    SortKey.Year to "Year",
    SortKey.Duration to "Duration",
)

/** Text keys read ascending by default; everything else newest/largest first. */
fun defaultAscending(key: SortKey): Boolean =
    key == SortKey.Title || key == SortKey.Artist || key == SortKey.Album

fun sortTracks(
    tracks: List<Track>,
    key: SortKey,
    ascending: Boolean,
    likedAtOf: (Track) -> Long,
): List<Track> {
    val collator = collator()
    val comparator: Comparator<Track> = when (key) {
        SortKey.Added -> compareBy { it.addedAtMs }
        SortKey.Liked -> compareBy { likedAtOf(it) }
        SortKey.Spotify -> compareBy { it.spotifyAddedAtMs }
        SortKey.Year -> compareBy { it.year ?: 0 }
        SortKey.Duration -> compareBy { it.durationSeconds }
        SortKey.Title -> Comparator { a, b -> collator.compare(a.title, b.title) }
        SortKey.Artist -> Comparator { a, b -> collator.compare(a.albumArtist, b.albumArtist) }
        SortKey.Album -> Comparator { a, b -> collator.compare(a.album, b.album) }
    }
    return tracks.sortedWith(if (ascending) comparator else comparator.reversed())
}

/**
 * The Spotify Liked chip carries its own order: newest save first, matching how the tracks appear in
 * Spotify itself. Releasing it restores the default rather than leaving the user on a sort key
 * nothing else can reach. Only fires on a *transition*, so pressing an unrelated chip never disturbs
 * a sort the user chose.
 */
fun sortForChipChange(
    previous: Set<ChipKey>,
    next: Set<ChipKey>,
    currentKey: SortKey,
    currentAscending: Boolean,
): Pair<SortKey, Boolean> {
    val wanted = ChipKey.SpotifyLiked in next
    if (wanted == (ChipKey.SpotifyLiked in previous)) return currentKey to currentAscending
    return if (wanted) SortKey.Spotify to false else SortKey.Added to false
}

/** Search over title, lead artist and album — the same three fields the web matches on. */
fun searchTracks(tracks: List<Track>, query: String): List<Track> {
    val q = query.trim()
    if (q.isEmpty()) return tracks
    return tracks.filter {
        it.title.contains(q, ignoreCase = true) ||
            it.albumArtist.contains(q, ignoreCase = true) ||
            it.album.contains(q, ignoreCase = true)
    }
}
