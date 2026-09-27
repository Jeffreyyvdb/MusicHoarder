package com.musichoarder.app.data

import kotlinx.serialization.Serializable

/**
 * The collected remote playlist a playlist follows (`GET /api/playlists`' `source`). Its tracks play
 * first, in the remote order; [type] is `spotifyLiked`, `spotifyPlaylist`, `deezer` or `youtube`.
 */
@Serializable
data class PlaylistSourceDto(
    val id: Int,
    val type: String,
    val name: String = "",
    val imageUrl: String? = null,
    val url: String? = null,
)

/**
 * One playlist as the server sends it: song ids in play order, joined against the `/songs` list the
 * phone already holds — the contract album cards and the radio use.
 */
@Serializable
data class PlaylistDto(
    val id: Int,
    val name: String,
    val source: PlaylistSourceDto? = null,
    /** What plays, in order: a synced playlist's own tracks, then the ones added in MusicHoarder. */
    val songIds: List<Int> = emptyList(),
    /** The ones added in MusicHoarder — the only ones that can be removed here. */
    val addedSongIds: List<Int> = emptyList(),
    /** A synced playlist's tracks not in the library yet. */
    val missingCount: Int = 0,
    val updatedAtUtc: String? = null,
)

@Serializable
data class PlaylistsResponse(val playlists: List<PlaylistDto> = emptyList())

@Serializable
data class PlaylistSongsResult(val added: Int = 0, val alreadyPresent: Int = 0, val playlist: PlaylistDto)

@Serializable
internal data class CreatePlaylistBody(val name: String, val songIds: List<Int>)

@Serializable
internal data class PlaylistSongsBody(val songIds: List<Int>)

@Serializable
internal data class RenamePlaylistBody(val name: String)

/** A playlist joined against the library: the rows it plays, and which of them were added here. */
data class Playlist(
    val id: Int,
    val name: String,
    val source: PlaylistSourceDto?,
    val tracks: List<Track>,
    val addedIds: Set<Int>,
    val missingCount: Int,
    val updatedAtUtc: String?,
)

/**
 * Joins each playlist's ids against [tracksById], reusing the repository's own [Track] objects (so
 * the like overlay reads the same rows as everywhere else). An id the phone does not hold — a song
 * that left the library, or one still loading — is skipped, as the web's `playlistSongs` does.
 */
fun hydratePlaylists(dtos: List<PlaylistDto>, tracksById: Map<Int, Track>): List<Playlist> =
    dtos.map { dto ->
        Playlist(
            id = dto.id,
            name = dto.name,
            source = dto.source,
            tracks = dto.songIds.mapNotNull { tracksById[it] },
            addedIds = dto.addedSongIds.toSet(),
            missingCount = dto.missingCount,
            updatedAtUtc = dto.updatedAtUtc,
        )
    }

/** Where a synced playlist comes from, as a short label ("Spotify", "YouTube"). The web's `sourceLabel`. */
fun sourceLabel(type: String): String = when (type) {
    "spotifyLiked", "spotifyPlaylist" -> "Spotify"
    "deezer" -> "Deezer"
    "youtube" -> "YouTube"
    else -> "Synced"
}

/** "1 track", "42 tracks". */
fun trackCountLabel(count: Int): String = "${grouped(count)} ${if (count == 1) "track" else "tracks"}"

/** The line under a playlist's name in a list — "Spotify · 42 tracks", "12 tracks", "YouTube · Empty". */
fun playlistSubtitle(source: PlaylistSourceDto?, trackCount: Int): String {
    val count = if (trackCount == 0) "Empty" else trackCountLabel(trackCount)
    return if (source != null) "${sourceLabel(source.type)} · $count" else count
}

/** What a synced playlist is still waiting for: "8 not in your library yet", or null. */
fun missingLabel(source: PlaylistSourceDto?, missingCount: Int): String? =
    if (source == null || missingCount <= 0) null else "${grouped(missingCount)} not in your library yet"

/** The kind of playlist, for its page ("Playlist", "Spotify playlist", "Spotify Liked Songs"). */
fun playlistKindLabel(source: PlaylistSourceDto?): String = when {
    source == null -> "Playlist"
    source.type == "spotifyLiked" -> "Spotify Liked Songs"
    else -> "${sourceLabel(source.type)} playlist"
}

private fun grouped(count: Int): String = "%,d".format(count)

/** Most recently changed first — the order the "Add to playlist" sheet offers them in. */
fun recentFirst(playlists: List<Playlist>): List<Playlist> =
    playlists.sortedByDescending { it.updatedAtUtc.orEmpty() }
