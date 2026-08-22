package com.musichoarder.app.data

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import okhttp3.Interceptor
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import java.io.IOException

/** Thrown when a call needs a pairing this phone does not have (yet). */
class NotPairedException : IOException("This device is not paired with a MusicHoarder server.")

/** Thrown when the server rejected the token — the pairing was revoked or expired. */
class UnauthorizedException : IOException("The pairing was revoked. Scan a new code to sign in again.")

class ApiException(val status: Int, message: String) : IOException(message)

/**
 * Attaches the bearer token — and only to the paired server.
 *
 * Registered as a *network* interceptor so every redirect hop is re-checked: some endpoints bounce
 * to third-party CDNs (artist portraits redirect to Deezer), and the credential must not ride
 * along. The follow-up request OkHttp builds for a redirect is derived from the original, which
 * never carried the header, so nothing leaks off-host.
 */
class AuthInterceptor(private val sessions: SessionStore) : Interceptor {
    override fun intercept(chain: Interceptor.Chain): Response {
        val request = chain.request()
        val session = sessions.session.value ?: return chain.proceed(request)
        val base = session.baseUrl.toHttpUrlOrNull() ?: return chain.proceed(request)
        // Scheme included deliberately: a redirect to http:// on the same host and effective port
        // would otherwise get the token re-attached to a cleartext hop, which is the one thing this
        // per-hop check exists to stop.
        val sameOrigin = request.url.scheme == base.scheme &&
            request.url.host == base.host &&
            request.url.port == base.port
        if (!sameOrigin) return chain.proceed(request)
        return chain.proceed(
            request.newBuilder().header("Authorization", "Bearer ${session.token}").build()
        )
    }
}

/**
 * Talks to a MusicHoarder deployment through the frontend's same-origin `/api/mh` proxy — the exact
 * surface the web app uses, so nothing new has to be exposed for the phone.
 */
class MusicHoarderApi(
    private val client: OkHttpClient,
    private val sessions: SessionStore,
) {
    private val json = Json { ignoreUnknownKeys = true; isLenient = true }

    private fun baseUrl(): String = sessions.session.value?.baseUrl ?: throw NotPairedException()

    private fun url(path: String): String = "${baseUrl()}$API_PREFIX$path"

    /** Range-enabled audio stream; prefers the built destination copy server-side. */
    fun streamUrl(songId: Int): String = url("/songs/$songId/stream")

    /**
     * Cover art, thumbnailed server-side. [size] is snapped up to the nearest bucket
     * (128/256/400/640), so passing the real display size costs nothing.
     */
    fun coverUrl(songId: Int, size: Int): String = url("/songs/$songId/cover?size=$size")

    /**
     * Identity check. [candidate] lets the pairing flow probe a session that has not been persisted
     * yet — the token has to be proven before it replaces a working one, so it cannot come from the
     * store, and the interceptor cannot supply it either.
     */
    suspend fun fetchMe(candidate: ServerSession? = null): AuthMe = withContext(Dispatchers.IO) {
        val base = candidate?.baseUrl ?: baseUrl()
        val request = Request.Builder()
            .url("$base$API_PREFIX/api/auth/me")
            .apply { candidate?.let { header("Authorization", "Bearer ${it.token}") } }
            .get()
            .build()
        execute(request) { json.decodeFromString<AuthMe>(it) }
    }

    suspend fun fetchSongs(): List<ApiSong> =
        get("/songs") { json.decodeFromString<SongsResponse>(it).songs }

    /**
     * Lyrics are fetched per song rather than shipped with the library dump — the AI transcription
     * text in particular is large, and most songs never have their lyrics opened.
     */
    suspend fun fetchLyrics(songId: Int): Lyrics =
        get("/api/tracks/$songId/lyrics") { json.decodeFromString<LyricsResponse>(it).toLyrics() }

    /** Null when the song has no music video attached (the endpoint 404s, which is the common case). */
    suspend fun fetchVideoInfo(songId: Int): VideoInfo? =
        try {
            get("/songs/$songId/video") { json.decodeFromString<VideoInfo>(it) }
        } catch (e: ApiException) {
            if (e.status == 404) null else throw e
        }

    /** Range-enabled mp4 of the music video, played muted behind the player. */
    fun videoStreamUrl(songId: Int): String = url("/songs/$songId/video/stream")

    /** Bumps play count / last-played, same as the web player does on track start. */
    suspend fun reportPlayed(songId: Int) {
        runCatching { post("/songs/$songId/played") }
    }

    /**
     * The heart. Unlike [reportPlayed] these throw: the caller flips its copy optimistically and has
     * to be able to put it back, which it cannot do if the failure is swallowed here.
     */
    suspend fun likeSong(songId: Int) = post("/songs/$songId/like")

    suspend fun unlikeSong(songId: Int) = delete("/songs/$songId/like")

    private suspend fun <T> get(path: String, parse: (String) -> T): T = withContext(Dispatchers.IO) {
        execute(Request.Builder().url(url(path)).get().build()) { parse(it) }
    }

    private suspend fun post(path: String) = withContext(Dispatchers.IO) {
        execute(Request.Builder().url(url(path)).post(EMPTY_BODY).build()) { }
    }

    private suspend fun delete(path: String) = withContext(Dispatchers.IO) {
        execute(Request.Builder().url(url(path)).delete().build()) { }
    }

    private fun <T> execute(request: Request, parse: (String) -> T): T =
        client.newCall(request).execute().use { response ->
            if (response.code == 401 || response.code == 403) throw UnauthorizedException()
            if (!response.isSuccessful) throw ApiException(response.code, "Request failed: ${response.code}")
            parse(response.body.string())
        }

    companion object {
        /** The frontend route that proxies to the API, header-for-header. */
        const val API_PREFIX = "/api/mh"
        private val EMPTY_BODY = ByteArray(0).toRequestBody(null)
    }
}
