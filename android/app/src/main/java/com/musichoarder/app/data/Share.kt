package com.musichoarder.app.data

import kotlinx.serialization.Serializable

/**
 * `GET /api/share/{token}` — the anonymous share payload, mirroring the web's `share-client.ts`.
 * Scope is Song or Album only; artist/library sharing is the authenticated friend surface.
 */
@Serializable
data class SharePayload(
    val scope: String = "Song",
    val sharedSongId: Int = 0,
    val album: ShareAlbum = ShareAlbum(),
    val tracks: List<ShareTrackDto> = emptyList(),
)

@Serializable
data class ShareAlbum(
    val title: String? = null,
    val artist: String? = null,
    val year: Int? = null,
)

@Serializable
data class ShareTrackDto(
    val id: Int,
    val title: String = "",
    val artist: String? = null,
    val trackNumber: Int? = null,
    val discNumber: Int? = null,
    val durationMs: Long? = null,
    val hasCoverArt: Boolean = false,
    val hasSyncedLyrics: Boolean = false,
    val hasPlainLyrics: Boolean = false,
    val isInstrumental: Boolean = false,
    val hasVideo: Boolean = false,
    val videoOffsetMs: Long? = null,
    val videoDurationSeconds: Int? = null,
)

/** `GET /api/invite/{token}` — who invited you, for which email, without consuming the token. */
@Serializable
data class InvitePeek(
    val inviterName: String? = null,
    val email: String? = null,
)

/** Body of `POST /api/invite/accept-token`. The response is the shared [AccessTokenResponse]. */
@Serializable
data class AcceptInviteBody(val token: String)

/**
 * Adapts a shared track to the library's [Track] so the row, queue, and player reuse the existing
 * plumbing. Share ids belong to the sharing server, so everything owner-ish (likes, play stats,
 * review flags) is zeroed; [streamUrl] and [artworkUrl] carry the absolute token-in-path URLs the
 * player must use instead of the paired routes.
 */
fun ShareTrackDto.toTrack(album: ShareAlbum, streamUrl: String, artworkUrl: String?): Track {
    val trackArtist = artist?.takeIf { it.isNotBlank() } ?: album.artist?.takeIf { it.isNotBlank() } ?: UNKNOWN_ARTIST
    val leadArtist = album.artist?.takeIf { it.isNotBlank() } ?: trackArtist
    val albumName = album.title?.takeIf { it.isNotBlank() } ?: UNKNOWN_ALBUM
    val nameKey = "${leadArtist.lowercase()}::${albumName.lowercase()}"
    return Track(
        id = id,
        title = title.ifBlank { "Untitled" },
        artist = trackArtist,
        album = albumName,
        albumArtist = leadArtist,
        artists = listOf(trackArtist),
        trackNumber = trackNumber,
        year = album.year,
        durationMs = durationMs,
        durationSeconds = (durationMs?.let { it / 1000 } ?: 0L).toInt(),
        hasCover = hasCoverArt,
        folderKey = nameKey,
        nameKey = nameKey,
        addedAtMs = 0L,
        likedAtMs = 0L,
        spotifyLikedAtMs = 0L,
        spotifyAddedAtMs = 0L,
        lastPlayedAtMs = 0L,
        likedAtUtc = null,
        playCount = 0,
        isSpotifyLiked = false,
        isLocalFile = false,
        isAddedByLink = false,
        hasVideo = hasVideo,
        hasLyrics = hasSyncedLyrics || hasPlainLyrics,
        isUnreleased = false,
        isAlbumFill = false,
        needsReview = false,
        streamUrl = streamUrl,
        artworkUrl = artworkUrl,
    )
}
