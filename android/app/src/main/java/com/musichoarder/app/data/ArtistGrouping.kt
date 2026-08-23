package com.musichoarder.app.data

/**
 * One artist's aggregated slice of the library. Port of the web's `GroupSummary`.
 */
data class ArtistGroup(
    /** The display name. Grouping dedupes case-insensitively, but the label is what is shown. */
    val key: String,
    val label: String,
    /**
     * Distinct album *names* (`artistLower::albumLower`), not destination folders. An artist whose
     * album is split across two folders counts it once here, so this number can legitimately be
     * lower than the number of cards you see after drilling in.
     */
    val albumCount: Int,
    val trackCount: Int,
    /** `A`–`Z`, or `#` for anything else. Precomputed so the A–Z bar never recomputes it per frame. */
    val initial: String,
    /** A track carrying artwork, used as the portrait's fallback. */
    val coverTrack: Track?,
)

/**
 * Group tracks by individual artist, sorted alphabetically. A multi-artist track contributes to each
 * of its credited artists' groups.
 *
 * With [primaryOnly] only *lead* artists are surfaced: a credited artist is kept only if they lead
 * at least one track. Featured/guest-only artists who never front a release are dropped, so the grid
 * shows album artists rather than every performer — but a lead artist's card still aggregates the
 * tracks where they only feature. Port of `buildArtistGroups`.
 */
fun buildArtistGroups(tracks: List<Track>, primaryOnly: Boolean): List<ArtistGroup> {
    val leadKeys = if (primaryOnly) tracks.mapTo(HashSet()) { it.albumArtist.lowercase() } else null

    class Accumulator(val label: String) {
        val albumKeys = HashSet<String>()
        var trackCount = 0
        var coverTrack: Track? = null
    }

    val groups = LinkedHashMap<String, Accumulator>()
    for (track in tracks) {
        for (label in track.artists) {
            val key = label.lowercase()
            if (leadKeys != null && key !in leadKeys) continue
            val entry = groups.getOrPut(key) { Accumulator(label) }
            entry.trackCount += 1
            entry.albumKeys.add(track.nameKey)
            if (entry.coverTrack == null && track.hasCover) entry.coverTrack = track
        }
    }

    val collator = collator()
    return groups.values
        .map {
            ArtistGroup(
                key = it.label,
                label = it.label,
                albumCount = it.albumKeys.size,
                trackCount = it.trackCount,
                initial = artistInitial(it.label),
                coverTrack = it.coverTrack,
            )
        }
        .sortedWith { a, b -> collator.compare(a.label, b.label) }
}

/** The A–Z bucket a name falls in. Anything not starting with a Latin letter lands under `#`. */
fun artistInitial(label: String): String {
    val first = label.trim().firstOrNull()?.uppercaseChar() ?: return "#"
    return if (first in 'A'..'Z') first.toString() else "#"
}

/**
 * Whether a track belongs to an artist's drill-down: it matches on the lead artist **or** on any
 * discrete credit, case-insensitively — so a multi-artist track is reachable from each artist the
 * grid lists it under. Port of `applyBrowseFilter`.
 */
fun matchesArtist(track: Track, name: String): Boolean {
    val target = name.trim().lowercase()
    if (target.isEmpty()) return true
    if (track.albumArtist.lowercase() == target) return true
    return track.artists.any { it.lowercase() == target }
}
