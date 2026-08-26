package com.musichoarder.app.data

import java.text.Collator
import java.util.Locale
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/**
 * One album card exactly as `GET /api/albums` groups it.
 *
 * The grouping rules live on the server. They used to live here, in a hand-written port of
 * `buildAlbumsFromSongs` in `frontend/src/lib/api-client.ts`, with the web keeping its own copy —
 * which is how the same album added-date rule came to need fixing twice, and how the phone and the
 * browser came to disagree about which track names a card. Anything here that looks like a grouping
 * decision is a bug: this side joins and orders, nothing more.
 */
@Serializable
data class AlbumSummaryDto(
    /** The representative destination folder, and what the album drill-down is addressed by. */
    @SerialName("key") val key: String,
    /**
     * Every destination folder this card covers — all of the merged folders, representative first.
     * Lets an open album survive a merge that elected a different folder.
     */
    @SerialName("folderKeys") val folderKeys: List<String> = emptyList(),
    /** `artistLower::albumLower` — the name-level identity, and the legacy deep-link shape. */
    @SerialName("nameKey") val nameKey: String,
    @SerialName("title") val title: String,
    @SerialName("artist") val artist: String,
    @SerialName("year") val year: Int? = null,
    @SerialName("trackCount") val trackCount: Int = 0,
    @SerialName("durationSeconds") val durationSeconds: Int = 0,
    @SerialName("playCount") val playCount: Int = 0,
    /** The first track with artwork; the album itself has no cover of its own. */
    @SerialName("coverSongId") val coverSongId: Int? = null,
    /** What "Recently added" sorts on — measured over your tracks, not album fill. */
    @SerialName("addedAtUtc") val addedAtUtc: String? = null,
    /** The album's tracks in disc/track order. Join against the library dump. */
    @SerialName("trackIds") val trackIds: List<Int> = emptyList(),
)

@Serializable
data class AlbumsResponse(
    @SerialName("albums") val albums: List<AlbumSummaryDto> = emptyList(),
)

/** One album's worth of tracks, in disc/track order — a server card joined to the tracks it names. */
data class Album(
    val key: String,
    val folderKeys: List<String>,
    val nameKey: String,
    val name: String,
    val artist: String,
    val year: Int?,
    val trackCount: Int,
    val durationSeconds: Int,
    val playCount: Int,
    val addedAtMs: Long,
    val coverTrackId: Int?,
    val tracks: List<Track>,
)

/**
 * Join server album cards to the tracks this app holds.
 *
 * The tracks must be the very objects the repository keeps, not copies: likes and play counts are
 * held as overlays keyed on those rows, and an album that carried its own copies would show a stale
 * heart until the next refresh.
 */
fun hydrateAlbums(albums: List<AlbumSummaryDto>, tracksById: Map<Int, Track>): List<Album> =
    albums.map { album ->
        // A track can be missing when the two fetches straddle a library change; dropping it is the
        // honest answer, and the next refresh reconciles.
        val tracks = album.trackIds.mapNotNull { tracksById[it] }
        Album(
            key = album.key,
            folderKeys = album.folderKeys.ifEmpty { listOf(album.key) },
            nameKey = album.nameKey,
            name = album.title,
            artist = album.artist,
            year = album.year,
            trackCount = album.trackCount,
            durationSeconds = album.durationSeconds,
            playCount = album.playCount,
            addedAtMs = parseIsoUtcMillis(album.addedAtUtc),
            // Fall back to a local scan for a server too old to name the cover track.
            coverTrackId = album.coverSongId ?: tracks.firstOrNull { it.hasCover }?.id,
            tracks = tracks,
        )
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
 * one they happened to arrive in.
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

/**
 * The web sorts names with `localeCompare`; Kotlin's `compareTo` is codepoint-ordered, which would
 * put every lowercase name after every uppercase one and file "Ólafur" past "Z". A [Collator] is the
 * JVM's equivalent of `localeCompare` and keeps the two clients' grids in the same order — and in
 * the same order as the server, which uses the invariant culture for exactly this reason.
 */
internal fun collator(): Collator = Collator.getInstance(Locale.ROOT)

private fun byArtistThenTitle(): Comparator<Album> {
    val collator = collator()
    return Comparator { a, b ->
        val byArtist = collator.compare(a.artist, b.artist)
        if (byArtist != 0) byArtist else collator.compare(a.name, b.name)
    }
}
