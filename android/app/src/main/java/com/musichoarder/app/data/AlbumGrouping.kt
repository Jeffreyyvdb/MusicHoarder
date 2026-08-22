package com.musichoarder.app.data

import java.text.Collator
import java.util.Locale

/**
 * One album's worth of tracks, in disc/track order.
 *
 * Keyed on the **destination folder**, not on the artist/album tags — see [buildAlbums]. Pure
 * Kotlin so the grouping rules can be unit-tested against their JavaScript originals in
 * `frontend/src/lib/api-client.ts`.
 */
data class Album(
    /** The representative destination folder. Also what the album drill-down is addressed by. */
    val key: String,
    /**
     * Every destination folder this card covers — `[key]` for a plain card, and all of the merged
     * folders (representative first) for one produced by [mergeAlbumsByName]. Lets an open album
     * survive a merge that elected a different folder.
     */
    val folderKeys: List<String>,
    /** `artistLower::albumLower` — the name-level identity the merge folds on. */
    val nameKey: String,
    val name: String,
    val artist: String,
    val year: Int?,
    val trackCount: Int,
    val durationSeconds: Int,
    val playCount: Int,
    val addedAtMs: Long,
    val lastPlayedAtMs: Long,
    val tracks: List<Track>,
) {
    /** Cover art comes from a track — the album itself has no id on the API. */
    val coverTrackId: Int? get() = tracks.firstOrNull { it.hasCover }?.id
}

/**
 * Group tracks into albums.
 *
 * Built tracks group by their **destination folder** — the unit the music server reads, where the
 * library builder elects one reconciled release identity — so the phone splits one album name across
 * releases exactly the way the player does. Tracks with no destination path fall back to their name
 * key. Port of `buildAlbumsFromSongs`.
 */
fun buildAlbums(tracks: List<Track>): List<Album> {
    val groups = LinkedHashMap<String, MutableList<Track>>()
    for (track in tracks) groups.getOrPut(track.folderKey) { mutableListOf() }.add(track)

    return groups.map { (key, members) ->
        val sorted = members.sortedWith(BY_TRACK_NUMBER_THEN_TITLE)
        val lead = sorted.first()
        Album(
            key = key,
            folderKeys = listOf(key),
            nameKey = lead.nameKey,
            name = lead.album,
            artist = lead.albumArtist,
            // The EARLIEST year the tracks agree on: a deluxe re-issue's tracks carry the reissue
            // year, and the album is still the year it came out.
            year = sorted.mapNotNull { it.year }.filter { it > 0 }.minOrNull(),
            trackCount = sorted.size,
            durationSeconds = sorted.sumOf { it.durationSeconds },
            playCount = sorted.sumOf { it.playCount },
            addedAtMs = sorted.maxOf { it.addedAtMs },
            lastPlayedAtMs = sorted.maxOf { it.lastPlayedAtMs },
            tracks = sorted,
        )
    }.sortedWith(byArtistThenTitle())
}

/**
 * Fold cards that are the same album under a different destination folder into one.
 *
 * [buildAlbums] keys on the destination folder, which mirrors what the music server shows — but it
 * also means one album whose tracks disagree about the year or the artist spelling lands as two or
 * three adjacent, near-identical cards. For *browsing* that reads as noise, so the grid merges by
 * name.
 *
 * The largest constituent folder becomes the representative (ties broken on the key, so the choice
 * is stable across refetches); [Album.folderKeys] carries all of them so a drill-down into a folder
 * that lost the election can still be resolved. Port of `mergeAlbumsByName`.
 */
fun mergeAlbumsByName(albums: List<Album>): List<Album> {
    val groups = LinkedHashMap<String, MutableList<Album>>()
    for (album in albums) groups.getOrPut(album.nameKey) { mutableListOf() }.add(album)

    return groups.map { (_, group) ->
        if (group.size == 1) return@map group.first()
        val ordered = group.sortedWith(
            compareByDescending<Album> { it.trackCount }.thenBy { it.key }
        )
        val lead = ordered.first()
        val tracks = ordered.flatMap { it.tracks }.sortedWith(BY_TRACK_NUMBER_THEN_TITLE)
        lead.copy(
            folderKeys = ordered.map { it.key },
            year = ordered.mapNotNull { it.year }.minOrNull(),
            trackCount = ordered.sumOf { it.trackCount },
            durationSeconds = ordered.sumOf { it.durationSeconds },
            playCount = ordered.sumOf { it.playCount },
            addedAtMs = ordered.maxOf { it.addedAtMs },
            lastPlayedAtMs = ordered.maxOf { it.lastPlayedAtMs },
            tracks = tracks,
        )
    }.sortedWith(byArtistThenTitle())
}

/** How the album grid is ordered. */
enum class AlbumSortKey { Recent, Artist, Title, Year, Played }

/** Label for each order, matching the web's `ALBUM_SORT_OPTIONS` wording. */
val ALBUM_SORT_LABELS: Map<AlbumSortKey, String> = mapOf(
    AlbumSortKey.Recent to "Recently added",
    AlbumSortKey.Artist to "Artist A–Z",
    AlbumSortKey.Title to "Album title",
    AlbumSortKey.Year to "Year (newest)",
    AlbumSortKey.Played to "Most played",
)

/**
 * Order albums for the grid. Every comparator falls back to artist-then-title, so albums that tie
 * (no play count, no year, same day added) keep a stable alphabetical order instead of the arbitrary
 * one the grouping map happened to produce.
 */
fun sortAlbums(albums: List<Album>, key: AlbumSortKey): List<Album> {
    val fallback = byArtistThenTitle()
    val primary: Comparator<Album> = when (key) {
        AlbumSortKey.Recent -> compareByDescending { it.addedAtMs }
        // Artist order IS the fallback; its own comparator deliberately does nothing.
        AlbumSortKey.Artist -> Comparator { _, _ -> 0 }
        AlbumSortKey.Title -> Comparator { a, b -> collator().compare(a.name, b.name) }
        AlbumSortKey.Year -> compareByDescending { it.year ?: 0 }
        AlbumSortKey.Played -> compareByDescending { it.playCount }
    }
    return albums.sortedWith(primary.then(fallback))
}

private val BY_TRACK_NUMBER_THEN_TITLE: Comparator<Track> =
    compareBy<Track> { it.trackNumber ?: Int.MAX_VALUE }.thenBy { it.title.lowercase() }

/**
 * The web sorts names with `localeCompare`; Kotlin's `compareTo` is codepoint-ordered, which would
 * put every lowercase name after every uppercase one and file "Ólafur" past "Z". A [Collator] is the
 * JVM's equivalent of `localeCompare` and keeps the two clients' grids in the same order.
 */
internal fun collator(): Collator = Collator.getInstance(Locale.ROOT)

private fun byArtistThenTitle(): Comparator<Album> {
    val collator = collator()
    return Comparator { a, b ->
        val byArtist = collator.compare(a.artist, b.artist)
        if (byArtist != 0) byArtist else collator.compare(a.name, b.name)
    }
}
