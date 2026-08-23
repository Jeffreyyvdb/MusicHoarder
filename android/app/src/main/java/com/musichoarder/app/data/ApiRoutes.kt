package com.musichoarder.app.data

/**
 * The per-song and library paths (relative to the `/api/mh` proxy prefix), switched on the
 * session's role — the Android twin of the web app's library mode.
 *
 * An owner reads their own rows through the tenancy-filtered endpoints; a `Friend` account owns no
 * song rows at all and reads exclusively through the grant-scoped `/api/shared` surface, whose
 * responses are deliberately shape-compatible (same envelope, reduced fields, and like/play values
 * that are the friend's own state). Kept as a pure object so the owner↔shared pairs are pinned by
 * plain JUnit tests without any HTTP machinery.
 */
internal object ApiRoutes {
    fun songs(friend: Boolean) = if (friend) "/api/shared/songs" else "/songs"

    fun stream(id: Int, friend: Boolean) = "${song(id, friend)}/stream"

    fun cover(id: Int, size: Int, friend: Boolean) = "${song(id, friend)}/cover?size=$size"

    /** The owner's lyrics route predates the per-song shape; the shared one lives with its song. */
    fun lyrics(id: Int, friend: Boolean) =
        if (friend) "${song(id, true)}/lyrics" else "/api/tracks/$id/lyrics"

    fun video(id: Int, friend: Boolean) = "${song(id, friend)}/video"

    fun videoStream(id: Int, friend: Boolean) = "${song(id, friend)}/video/stream"

    fun like(id: Int, friend: Boolean) = "${song(id, friend)}/like"

    fun played(id: Int, friend: Boolean) = "${song(id, friend)}/played"

    private fun song(id: Int, friend: Boolean) = if (friend) "/api/shared/songs/$id" else "/songs/$id"
}
