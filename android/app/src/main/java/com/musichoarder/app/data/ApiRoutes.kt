package com.musichoarder.app.data

import java.net.URLEncoder

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

    /**
     * Album cards, grouped server-side. Query-free: the phone shows the whole grid and narrows it
     * in memory, so it only ever asks for the default (built songs, folders merged by name).
     */
    fun albums() = "/api/albums"

    /**
     * What to play once the queue runs dry — ids ordered by similarity to [seedSongId]. The ranking
     * is the server's (`MusicHoarder.Api/Library/RadioRanker.cs`), so the phone and the web app
     * build the same station instead of each having their own idea of "similar".
     */
    fun radio(seedSongId: Int, exclude: List<Int>, limit: Int): String {
        val excludeParam = if (exclude.isEmpty()) "" else "&exclude=${exclude.joinToString(",")}"
        return "/api/radio?seedSongId=$seedSongId&limit=$limit$excludeParam"
    }

    /** The account's playlists — made here, and one per collected Spotify/Deezer/YouTube playlist. */
    fun playlists() = "/api/playlists"

    fun playlist(id: Int) = "/api/playlists/$id"

    /** POST appends songs to a playlist. */
    fun playlistSongs(id: Int) = "${playlist(id)}/songs"

    /** DELETE removes one song added in MusicHoarder. */
    fun playlistSong(id: Int, songId: Int) = "${playlistSongs(id)}/$songId"

    fun stream(id: Int) = "${song(id)}/stream"

    fun cover(id: Int, size: Int) = "${song(id)}/cover?size=$size"

    /** Lyrics predate the per-song shape and keep their own route. */
    fun lyrics(id: Int) = "/api/tracks/$id/lyrics"

    fun video(id: Int) = "${song(id)}/video"

    fun videoStream(id: Int) = "${song(id)}/video/stream"

    fun like(id: Int) = "${song(id)}/like"

    fun played(id: Int) = "${song(id)}/played"

    /** The account's playback session and its devices ("Connect"). */
    fun playback() = "/api/playback"

    fun playbackState() = "/api/playback/state"

    fun playbackCommand() = "/api/playback/command"

    /**
     * The session's event stream. The device introduces itself in the query rather than a header,
     * because the stream is a plain GET that registers the device for as long as it stays open.
     */
    fun playbackStream(deviceId: String, installId: String?, name: String, kind: String, client: String): String =
        "/api/playback/stream?deviceId=${query(deviceId)}&installId=${query(installId.orEmpty())}" +
            "&name=${query(name)}&kind=${query(kind)}&client=${query(client)}"

    private fun song(id: Int) = "/songs/$id"

    /** `%20` rather than `+` for a space: a device name crosses the frontend proxy too. */
    private fun query(value: String): String = URLEncoder.encode(value, "UTF-8").replace("+", "%20")
}
