package com.musichoarder.app.data

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonPrimitive

/**
 * The slice of `GET /songs` this client cares about. The endpoint returns the whole enrichment
 * record per row; [kotlinx.serialization.json.Json.ignoreUnknownKeys] drops the rest.
 */
@Serializable
data class ApiSong(
    val id: Int,
    val fileName: String = "",
    val title: String? = null,
    val artist: String? = null,
    val albumArtist: String? = null,
    val album: String? = null,
    val year: Int? = null,
    val trackNumber: Int? = null,
    val durationSeconds: Int? = null,
    val durationMs: Int? = null,
    val hasCoverArt: Boolean = false,
    val acquiredAtUtc: String? = null,
    /** The local heart, set by `POST /songs/{id}/like`. Null means not liked. */
    val likedAtUtc: String? = null,
    val destinationPath: String? = null,
    /** Serialized as a number by `/songs`, but the web's type allows the enum name too. */
    val libraryBuildStatus: JsonPrimitive? = null,
) {
    val isLiked: Boolean get() = !likedAtUtc.isNullOrBlank()

    /**
     * A song is "built" once it reached the destination library: `LibraryBuildStatus == Done` and a
     * destination path is set. That implies it was enriched and matched first.
     *
     * This is the port of `isBuiltSong` in `frontend/src/lib/album-sections.ts`, and every "Listen"
     * surface on the web filters by it. Without the same filter the phone lists raw scanned rows the
     * web deliberately never shows — including untagged files with no artist or title — so the two
     * clients disagree about what is even in the library.
     */
    val isBuilt: Boolean
        get() {
            if (destinationPath.isNullOrBlank()) return false
            val status = libraryBuildStatus?.content ?: return false
            return status.toIntOrNull()?.let { it == LIBRARY_BUILD_DONE }
                ?: status.equals("Done", ignoreCase = true)
        }
}

/** `LibraryBuildStatus.Done` — Pending/Copied/Tagged/Done. */
private const val LIBRARY_BUILD_DONE = 3

@Serializable
data class SongsResponse(@SerialName("songs") val songs: List<ApiSong> = emptyList())

@Serializable
data class AuthMe(val email: String? = null, val role: String? = null, val displayName: String? = null)

/** A playable row, with every display field already resolved so the UI never re-decides. */
data class Track(
    val id: Int,
    val title: String,
    val artist: String,
    val album: String,
    val albumArtist: String,
    val trackNumber: Int?,
    val year: Int?,
    val durationMs: Long?,
    val hasCover: Boolean,
    val acquiredAtUtc: String?,
) {
    /** Groups tracks into albums the way the destination library lays them out on disk. */
    val albumKey: String get() = "$albumArtist $album"
}

const val UNKNOWN_ARTIST = "Unknown artist"
const val UNKNOWN_ALBUM = "Unknown album"

fun ApiSong.toTrack(): Track {
    val artist = artist?.takeIf { it.isNotBlank() } ?: UNKNOWN_ARTIST
    val albumArtist = albumArtist?.takeIf { it.isNotBlank() } ?: artist
    return Track(
        id = id,
        // A song that never matched has no title tag; its filename is the only name it has.
        title = title?.takeIf { it.isNotBlank() } ?: fileName.substringBeforeLast('.').ifBlank { "Untitled" },
        artist = artist,
        album = album?.takeIf { it.isNotBlank() } ?: UNKNOWN_ALBUM,
        albumArtist = albumArtist,
        trackNumber = trackNumber,
        year = year,
        durationMs = durationMs?.toLong() ?: durationSeconds?.let { it * 1000L },
        hasCover = hasCoverArt,
        acquiredAtUtc = acquiredAtUtc,
    )
}

/** One album's worth of tracks, in disc/track order. */
data class Album(
    val key: String,
    val name: String,
    val artist: String,
    val year: Int?,
    val tracks: List<Track>,
) {
    /** Cover art comes from a track — the album itself has no id on the API. */
    val coverTrackId: Int? get() = tracks.firstOrNull { it.hasCover }?.id
}

fun List<Track>.toAlbums(): List<Album> =
    groupBy { it.albumKey }
        .map { (key, tracks) ->
            val sorted = tracks.sortedWith(compareBy({ it.trackNumber ?: Int.MAX_VALUE }, { it.title }))
            Album(
                key = key,
                name = sorted.first().album,
                artist = sorted.first().albumArtist,
                year = sorted.firstNotNullOfOrNull { it.year },
                tracks = sorted,
            )
        }
        .sortedWith(compareBy({ it.artist.lowercase() }, { it.name.lowercase() }))
