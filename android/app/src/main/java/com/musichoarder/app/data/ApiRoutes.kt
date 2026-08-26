package com.musichoarder.app.data

/**
 * The per-song and library paths, relative to the `/api/mh` proxy prefix.
 *
 * These used to fork on the session's role: an invited account read through a parallel
 * `/api/shared` surface while everyone else used the ordinary endpoints. The server now scopes
 * every one of these to the caller — you get your own rows plus whatever was shared with you — so
 * there is one path per operation and the client needs no idea what kind of account it holds.
 *
 * Kept as a pure object so the paths stay pinned by plain JUnit tests without any HTTP machinery.
 */
internal object ApiRoutes {
    fun songs() = "/songs"

    fun stream(id: Int) = "${song(id)}/stream"

    fun cover(id: Int, size: Int) = "${song(id)}/cover?size=$size"

    /** Lyrics predate the per-song shape and keep their own route. */
    fun lyrics(id: Int) = "/api/tracks/$id/lyrics"

    fun video(id: Int) = "${song(id)}/video"

    fun videoStream(id: Int) = "${song(id)}/video/stream"

    fun like(id: Int) = "${song(id)}/like"

    fun played(id: Int) = "${song(id)}/played"

    private fun song(id: Int) = "/songs/$id"
}
