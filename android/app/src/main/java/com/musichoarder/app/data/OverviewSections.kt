package com.musichoarder.app.data

/** Everything the Overview tab renders, folded once per state change. */
data class OverviewSections(
    val favouriteTracks: List<Track> = emptyList(),
    val recentAlbums: List<Album> = emptyList(),
    val lastPlayedAlbums: List<Album> = emptyList(),
    val newToYouAlbums: List<Album> = emptyList(),
    val discoverAlbums: List<Album> = emptyList(),
    val artistsToRevisit: List<ArtistGroup> = emptyList(),
    val shelfAlbums: List<Album> = emptyList(),
)

private const val SHELF_SIZE = 12
private const val FAVOURITE_COUNT = 10

/** Port of `LibraryOverviewV2`'s greeting. */
fun greetingForHour(hour: Int): String = when {
    hour < 6 -> "Night owl session"
    hour < 12 -> "Good morning"
    hour < 18 -> "Good afternoon"
    else -> "Good evening"
}

/**
 * A stable per-visit shuffle: random each time you open the app, fixed while you are looking at it,
 * so the shelves do not rearrange under your thumb every time the library quietly refetches.
 *
 * The hash is the web's djb2, pinned as an **unsigned** 32-bit value - the same trap `AlbumTintTest`
 * was written to catch for `cyrb53`. Two deliberate departures from `seededOrder`, because the web's
 * version does not in fact shuffle:
 *
 * - The seed goes **first**. djb2 is `h * 33 + c`, so appending the seed multiplies every hash by
 *   the same constant and adds the same offset, which leaves the order almost entirely decided by
 *   the key. On the web that means the "random" shelves show very nearly the same albums every time.
 * - The result is run through murmur3's finalizer. Album keys are long paths sharing long prefixes,
 *   and raw djb2 files neighbouring keys next to each other; the avalanche step decorrelates them.
 *
 * Nothing compares these orders across clients, so diverging costs nothing here and is the
 * difference between a shuffle and a sort.
 */
fun <T> seededOrder(items: List<T>, seed: String, keyOf: (T) -> String): List<T> =
    items.sortedBy { scramble(djb2(seed + keyOf(it))) }

internal fun djb2(input: String): Long {
    var hash = 5381
    for (ch in input) hash = (hash shl 5) + hash + ch.code
    return hash.toLong() and 0xFFFF_FFFFL
}

/** murmur3's finalizer. Same constants as `cyrb53` in `Artwork.kt`, for the same reason. */
internal fun scramble(hash: Long): Long {
    var h = hash.toInt()
    h = (h xor (h ushr 16)) * -0x7A143595
    h = (h xor (h ushr 13)) * -0x3D4D51CB
    h = h xor (h ushr 16)
    return h.toLong() and 0xFFFF_FFFFL
}

/**
 * Builds the seven Overview shelves, in the order the web renders them.
 *
 * Play counts and likes come in as lambdas so the optimistic overlays move these sections without a
 * refetch — pressing play on a "never played" album should take it out of Discover, as it does on
 * the web.
 */
fun buildOverviewSections(
    tracks: List<Track>,
    albums: List<Album>,
    artists: List<ArtistGroup>,
    seed: String,
    likedAtMsOf: (Track) -> Long,
    playCountOf: (Track) -> Int,
    lastPlayedAtMsOf: (Track) -> Long,
): OverviewSections {
    val favourites = tracks
        .filter { likedAtMsOf(it) > 0L }
        .sortedByDescending { likedAtMsOf(it) }
        .take(FAVOURITE_COUNT)

    val lastPlayed = albums
        .map { it to it.tracks.maxOfOrNull(lastPlayedAtMsOf).orZero() }
        .filter { it.second > 0L }
        .sortedByDescending { it.second }
        .take(SHELF_SIZE)
        .map { it.first }

    val neverPlayed = albums.filter { album -> album.tracks.all { playCountOf(it) == 0 } }

    // "New to you": albums that album completion filled in and you have not played yet. This is the
    // payoff of that feature — you liked one track, here is the rest of the record, waiting.
    val newToYou = neverPlayed.filter { album ->
        album.tracks.any { it.isAlbumFill && likedAtMsOf(it) <= 0L }
    }

    return OverviewSections(
        favouriteTracks = favourites,
        recentAlbums = sortAlbums(albums, AlbumSortKey.Recent).take(SHELF_SIZE),
        lastPlayedAlbums = lastPlayed,
        newToYouAlbums = seededOrder(newToYou, seed) { it.key }.take(SHELF_SIZE),
        discoverAlbums = seededOrder(neverPlayed, seed) { it.key }.take(SHELF_SIZE),
        artistsToRevisit = seededOrder(artists, seed) { it.key }.take(SHELF_SIZE),
        shelfAlbums = seededOrder(albums, seed) { it.key }.take(SHELF_SIZE),
    )
}

private fun Long?.orZero(): Long = this ?: 0L
