package com.musichoarder.app.data

import kotlinx.serialization.Serializable

/** One `(artist, album)` pair to look the link status up for. */
@Serializable
data class AlbumIdentity(val artist: String, val album: String)

@Serializable
internal data class AlbumStatusRequest(val albums: List<AlbumIdentity>)

@Serializable
internal data class AlbumStatusResponse(
    val artist: String = "",
    val album: String = "",
    val status: String = "pending",
    val providers: List<String> = emptyList(),
    val verdict: String? = null,
)

@Serializable
data class LikeResponse(val id: Int = 0, val likedAtUtc: String? = null)

/**
 * Whether an album is linked to a provider's catalog — the corner dot on the album cards.
 *
 * A confirmed mis-match dominates regardless of link state, which is why [isWrong] is checked before
 * [status] at the call site.
 */
data class AlbumStatus(
    val status: String,
    val providers: List<String>,
    val verdict: String?,
) {
    val isWrong: Boolean get() = verdict.equals("Wrong", ignoreCase = true)
    val isLinked: Boolean get() = status.equals("linked", ignoreCase = true)
    val isLocalOnly: Boolean get() = status.equals("localOnly", ignoreCase = true)
}
