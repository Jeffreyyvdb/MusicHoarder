package com.musichoarder.app.data

/**
 * The per-song derivations the library views share, ported from the web.
 *
 * Deliberately pure Kotlin with no Android imports, so the unit tests can pin these against their
 * JavaScript originals the way `AlbumTintTest` and `ParseLrcTest` already do — the two clients have
 * to agree about what "recently added" or "liked" means, or the same library reads differently
 * depending on which one you opened.
 *
 * Every timestamp is epoch milliseconds, with **0 meaning "not known"** — the same convention the
 * web's `songAddedTime` / `songLikedTime` use, where an unparseable date collapses to 0 and sorts
 * last. It costs the first millisecond of 1970, which no music library has.
 */

/**
 * Parses the ISO-8601 stamps `/songs` returns, without `java.time`.
 *
 * `Instant.parse` is API 26 and this app ships `minSdk = 24` with no core-library desugaring, so
 * the arithmetic is done here. Two things this has to get right that the obvious
 * `SimpleDateFormat("...SSS")` does not:
 *
 * - .NET's round-trip format writes **seven** fractional digits (`.1234567Z`); `SSS` reads those as
 *   1,234,567 milliseconds and throws the timestamp two weeks into the future. Only the first three
 *   digits are milliseconds here, the rest are dropped.
 * - A stamp with no zone designator is read as **UTC**. Every column these come from is named
 *   `...AtUtc`; JavaScript would read the same string as local time, which is a latent bug on that
 *   side rather than behaviour worth reproducing.
 *
 * Returns 0 for null, blank, or anything that does not parse.
 */
fun parseIsoUtcMillis(raw: String?): Long {
    val s = raw?.trim().orEmpty()
    if (s.length < 10) return 0L
    if (s[4] != '-' || s[7] != '-') return 0L

    val year = s.substring(0, 4).toIntOrNull() ?: return 0L
    val month = s.substring(5, 7).toIntOrNull() ?: return 0L
    val day = s.substring(8, 10).toIntOrNull() ?: return 0L
    if (month !in 1..12 || day !in 1..31) return 0L

    var hour = 0
    var minute = 0
    var second = 0
    var millis = 0
    var offsetMinutes = 0

    if (s.length > 10) {
        if (s[10] != 'T' && s[10] != 't' && s[10] != ' ') return 0L
        var i = 11
        if (s.length < i + 5 || s[i + 2] != ':') return 0L
        hour = s.substring(i, i + 2).toIntOrNull() ?: return 0L
        minute = s.substring(i + 3, i + 5).toIntOrNull() ?: return 0L
        i += 5

        if (i < s.length && s[i] == ':') {
            if (s.length < i + 3) return 0L
            second = s.substring(i + 1, i + 3).toIntOrNull() ?: return 0L
            i += 3

            if (i < s.length && s[i] == '.') {
                var end = i + 1
                while (end < s.length && s[end].isDigit()) end++
                if (end == i + 1) return 0L
                millis = (s.substring(i + 1, end) + "000").substring(0, 3).toIntOrNull() ?: return 0L
                i = end
            }
        }

        if (i < s.length) {
            when (s[i]) {
                'Z', 'z' -> i += 1
                '+', '-' -> {
                    val sign = if (s[i] == '-') -1 else 1
                    i += 1
                    if (s.length < i + 2) return 0L
                    val offHour = s.substring(i, i + 2).toIntOrNull() ?: return 0L
                    i += 2
                    var offMinute = 0
                    if (i < s.length) {
                        if (s[i] == ':') i += 1
                        if (i < s.length) {
                            if (s.length < i + 2) return 0L
                            offMinute = s.substring(i, i + 2).toIntOrNull() ?: return 0L
                            i += 2
                        }
                    }
                    if (offHour > 23 || offMinute > 59) return 0L
                    offsetMinutes = sign * (offHour * 60 + offMinute)
                }
                else -> return 0L
            }
            if (i != s.length) return 0L
        }
    }

    if (hour > 23 || minute > 59 || second > 59) return 0L

    val days = daysFromCivil(year, month, day)
    val seconds = days * 86_400L + hour * 3_600L + minute * 60L + second - offsetMinutes * 60L
    return seconds * 1_000L + millis
}

/** Days between 1970-01-01 and the given civil date (Howard Hinnant's `days_from_civil`). */
private fun daysFromCivil(year: Int, month: Int, day: Int): Long {
    val y = if (month <= 2) year - 1 else year
    val era = (if (y >= 0) y else y - 399) / 400
    val yearOfEra = y - era * 400
    val dayOfYear = (153 * (if (month > 2) month - 3 else month + 9) + 2) / 5 + day - 1
    val dayOfEra = yearOfEra * 365 + yearOfEra / 4 - yearOfEra / 100 + dayOfYear
    return era * 146_097L + dayOfEra - 719_468L
}

/** The earliest of the candidates. 0 means "not known" and can never win. */
fun oldestMillis(vararg candidates: Long): Long {
    var oldest = 0L
    for (candidate in candidates) {
        if (candidate <= 0L) continue
        if (oldest == 0L || candidate < oldest) oldest = candidate
    }
    return oldest
}

/**
 * When a song entered the collection — the key "Recently added" sorts on.
 *
 * Port of `songAddedIso` in `frontend/src/lib/api-client.ts`. Two rules, both load-bearing:
 *
 * - `acquiredAtUtc` is the moment the file landed and is never rewritten. Rows predating that column
 *   fall back to the *oldest* of the two churn-prone stamps rather than preferring either: a
 *   re-index bumps `indexedAtUtc` while a rebuild clears and re-sets `libraryBuiltAtUtc`, so
 *   whichever survived un-bumped is the closer guess.
 * - An earlier Spotify save date beats all of them. For a wishlist download `acquiredAtUtc` is when
 *   the downloader got round to it, so a years-old save would otherwise drip in wearing today's
 *   stamp and land at the top of the shelf next to things you actually just got. This only ever
 *   pulls the date *backwards*, so a track ripped years before it was saved keeps its own date.
 */
fun songAddedMillis(
    spotifyAddedAt: Long,
    acquiredAt: Long,
    libraryBuiltAt: Long,
    indexedAt: Long,
): Long {
    val acquired = if (acquiredAt > 0L) acquiredAt else oldestMillis(libraryBuiltAt, indexedAt)
    return oldestMillis(spotifyAddedAt, acquired)
}

/**
 * When this song was liked, or 0 when it is not liked at all.
 *
 * `likedAtUtc` records when *this app* learned about the like: the Spotify auto-like sweep stamps
 * every song it matches in one pass with a single `now`, so thousands of rows tie on one instant and
 * "newest liked first" degenerates into the tie-break order. An earlier Spotify date is the real
 * like moment and wins — but only when the song is liked here, so an unliked track with a Spotify
 * save date still reports 0.
 */
fun songLikedMillis(likedAt: Long, spotifyLikedAt: Long, spotifyAddedAt: Long): Long {
    if (likedAt <= 0L) return 0L
    val spotify = if (spotifyLikedAt > 0L) spotifyLikedAt else spotifyAddedAt
    return oldestMillis(spotify, likedAt)
}

/**
 * The Spotify save date. Prefers the Liked Songs stamp: for a track that is both liked and in a
 * collected playlist, the moment you saved it is the meaningful one, and `spotifyAddedAtUtc` may
 * hold the playlist's add date instead.
 */
fun spotifyAddedMillis(spotifyLikedAt: Long, spotifyAddedAt: Long): Long =
    if (spotifyLikedAt > 0L) spotifyLikedAt else spotifyAddedAt

/** The pipeline's verdict on a song, normalised across the number and name forms `/songs` emits. */
enum class EnrichmentState { Pending, Processing, Complete, NeedsReview, Failed }

/** Port of `mapEnrichmentStatus`. Anything unrecognised is Pending, as on the web. */
fun mapEnrichmentState(raw: String?): EnrichmentState {
    val value = raw?.trim().orEmpty()
    if (value.isEmpty()) return EnrichmentState.Pending
    value.toIntOrNull()?.let {
        return when (it) {
            1 -> EnrichmentState.Complete
            2 -> EnrichmentState.NeedsReview
            3 -> EnrichmentState.Failed
            else -> EnrichmentState.Pending
        }
    }
    return when (value.lowercase()) {
        "failed" -> EnrichmentState.Failed
        "matched", "complete" -> EnrichmentState.Complete
        "needsreview" -> EnrichmentState.NeedsReview
        "running", "processing" -> EnrichmentState.Processing
        else -> EnrichmentState.Pending
    }
}

/**
 * The artist names a song is listed under: its discrete credited artists when the pipeline recorded
 * them (';'-joined), else the single lead label. A multi-artist track appears under each individual
 * artist rather than under one combined pseudo-artist.
 */
fun discreteArtists(artists: String?, leadLabel: String): List<String> {
    val discrete = artists.orEmpty()
        .split(';')
        .map { it.trim() }
        .filter { it.isNotEmpty() }
    return discrete.ifEmpty { listOf(leadLabel) }
}
