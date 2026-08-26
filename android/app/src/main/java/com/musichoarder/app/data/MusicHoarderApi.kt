package com.musichoarder.app.data

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import okhttp3.Interceptor
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import java.io.IOException
import java.net.URLEncoder

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
        // A request that already carries an Authorization header set it deliberately — the pairing
        // probe proving a *candidate* token (see MusicHoarderApi.fetchMe). Replacing it with the
        // active account's token would probe the wrong identity, and with multiple accounts on one
        // server the add-account dedupe would then fold the new account into the active one.
        if (request.header("Authorization") != null) return chain.proceed(request)
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
    fun streamUrl(songId: Int): String = url(ApiRoutes.stream(songId))

    /**
     * Cover art, thumbnailed server-side. [size] is snapped up to the nearest bucket
     * (128/256/400/640), so passing the real display size costs nothing.
     */
    fun coverUrl(songId: Int, size: Int): String = url(ApiRoutes.cover(songId, size))

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

    /**
     * Asks [baseUrl] to email a sign-in link for [email]. Enumeration-safe: the server answers
     * 200 whether or not the email belongs to an account, so "sent" only ever means "sent if it
     * exists". `client: "app"` makes the emailed link land on the browser handoff page that
     * offers the `musichoarder://auth` deep link back into this app.
     */
    suspend fun requestLoginLink(baseUrl: String, email: String): RequestLinkResponse =
        withContext(Dispatchers.IO) {
            val body = json.encodeToString(RequestLinkBody(email = email, client = "app"))
                .toRequestBody(JSON_MEDIA_TYPE)
            val request = Request.Builder()
                .url("$baseUrl$API_PREFIX/api/auth/request-link")
                // With no public base URL configured the server derives the link's origin from
                // the caller; a browser sends Origin on every POST, so the app does too.
                .header("Origin", baseUrl)
                .post(body)
                .build()
            execute(request) { json.decodeFromString<RequestLinkResponse>(it) }
        }

    /**
     * Exchanges a one-time magic-link token for the bearer token this phone will keep. Explicit
     * [baseUrl] because this runs before any session exists to read one from.
     */
    suspend fun exchangeLoginToken(baseUrl: String, token: String): String =
        withContext(Dispatchers.IO) {
            val body = json.encodeToString(TokenExchangeBody(token)).toRequestBody(JSON_MEDIA_TYPE)
            val request = Request.Builder()
                .url("$baseUrl$API_PREFIX/api/auth/token")
                .post(body)
                .build()
            execute(request) { json.decodeFromString<AccessTokenResponse>(it).accessToken }
        }

    /**
     * Starts a passkey sign-in. Explicit [baseUrl] because this runs before any session exists to
     * read one from, exactly like the magic-link pair above.
     */
    suspend fun beginPasskeySignIn(baseUrl: String): PasskeyChallengeResponse =
        withContext(Dispatchers.IO) {
            val request = Request.Builder()
                .url("$baseUrl$API_PREFIX/api/auth/webauthn/authenticate/native/begin")
                .post(EMPTY_JSON_BODY)
                .build()
            execute(request) { json.decodeFromString<PasskeyChallengeResponse>(it) }
        }

    /**
     * Finishes a passkey sign-in and returns the bearer this phone will keep. [assertionJson] is
     * Credential Manager's response, spliced in as an object rather than a string because the API
     * binds it to a typed WebAuthn assertion.
     */
    suspend fun completePasskeySignIn(baseUrl: String, state: String, assertionJson: String): String =
        withContext(Dispatchers.IO) {
            val body = buildJsonObject {
                put("state", state)
                put("assertion", json.parseToJsonElement(assertionJson))
            }.toString().toRequestBody(JSON_MEDIA_TYPE)
            val request = Request.Builder()
                .url("$baseUrl$API_PREFIX/api/auth/webauthn/authenticate/native/complete")
                .post(body)
                .build()
            execute(request) { json.decodeFromString<AccessTokenResponse>(it).accessToken }
        }

    /** The whole library: your own rows plus anything shared with you, and who shared it. */
    suspend fun fetchSongs(): SongsResponse =
        get(ApiRoutes.songs()) { json.decodeFromString<SongsResponse>(it) }

    /**
     * The album cards. Fetched alongside the library dump rather than derived from it: grouping
     * songs into albums is one set of rules, and it lives on the server now.
     *
     * Null when the server is older than that route. A phone from the Play Store can be newer than
     * the self-hosted server it talks to, and losing the album grid is no reason to refuse to show
     * the library at all — `/songs` answered perfectly well.
     */
    suspend fun fetchAlbums(): List<AlbumSummaryDto>? =
        try {
            get(ApiRoutes.albums()) { json.decodeFromString<AlbumsResponse>(it).albums }
        } catch (e: ApiException) {
            if (e.status == 404) null else throw e
        }

    /**
     * The station that follows [seedSongId] once the queue runs dry: song ids, ordered.
     *
     * Ids only — the caller already holds every row from [fetchSongs] and joins against it, the
     * same contract [fetchAlbums] uses for its track ids.
     *
     * Empty on 404, which covers both "the server predates this route" and "that seed is not a
     * song you may read". A phone from the Play Store can be newer than the self-hosted server it
     * talks to, and a queue that simply ends is exactly what that server already does.
     */
    suspend fun fetchRadio(seedSongId: Int, exclude: List<Int>, limit: Int = 20): List<Int> =
        try {
            get(ApiRoutes.radio(seedSongId, exclude, limit)) {
                json.decodeFromString<RadioResponse>(it).songIds
            }
        } catch (e: ApiException) {
            if (e.status == 404) emptyList() else throw e
        }

    /**
     * Lyrics are fetched per song rather than shipped with the library dump — the AI transcription
     * text in particular is large, and most songs never have their lyrics opened.
     */
    suspend fun fetchLyrics(songId: Int): Lyrics =
        get(ApiRoutes.lyrics(songId)) { json.decodeFromString<LyricsResponse>(it).toLyrics() }

    /** Null when the song has no music video attached (the endpoint 404s, which is the common case). */
    suspend fun fetchVideoInfo(songId: Int): VideoInfo? =
        try {
            get(ApiRoutes.video(songId)) { json.decodeFromString<VideoInfo>(it) }
        } catch (e: ApiException) {
            if (e.status == 404) null else throw e
        }

    /** Range-enabled mp4 of the music video, played muted behind the player. */
    fun videoStreamUrl(songId: Int): String = url(ApiRoutes.videoStream(songId))

    /**
     * The artist's portrait, by name.
     *
     * The endpoint **302s to Deezer or Spotify**, and 404s when nobody has a verified portrait. Both
     * are safe here: the bearer is attached by a network interceptor that re-checks the origin on
     * every hop, so the credential never rides the redirect off-server, and Coil follows it through
     * the same client. A 404 simply never paints, leaving the album cover underneath.
     */
    fun artistImageUrl(name: String): String =
        url("/api/artists/image?name=${URLEncoder.encode(name, "UTF-8")}")

    /** Hearts or un-hearts a song. Returns the server's `likedAtUtc` — null once unliked. */
    suspend fun setLiked(songId: Int, liked: Boolean): String? = withContext(Dispatchers.IO) {
        val request = Request.Builder()
            .url(url(ApiRoutes.like(songId)))
            .apply { if (liked) post(EMPTY_BODY) else delete() }
            .build()
        execute(request) { json.decodeFromString<LikeResponse>(it).likedAtUtc }
    }

    /**
     * Provider-link status for a batch of albums, keyed `artistLower::albumLower` — the corner dot on
     * the album cards. One request for the whole grid rather than one per tile.
     */
    suspend fun fetchAlbumStatuses(albums: List<AlbumIdentity>): Map<String, AlbumStatus> =
        withContext(Dispatchers.IO) {
            if (albums.isEmpty()) return@withContext emptyMap()
            val body = json.encodeToString(AlbumStatusRequest(albums)).toRequestBody(JSON_MEDIA_TYPE)
            val request = Request.Builder()
                .url(url("/api/albums/canonical-status"))
                .post(body)
                .build()
            execute(request) { payload ->
                json.decodeFromString<List<AlbumStatusResponse>>(payload).associate { row ->
                    "${row.artist.lowercase()}::${row.album.lowercase()}" to
                        AlbumStatus(row.status, row.providers, row.verdict)
                }
            }
        }

    /** Bumps play count / last-played, same as the web player does on track start. */
    suspend fun reportPlayed(songId: Int) {
        runCatching { post(ApiRoutes.played(songId)) }
    }

    // --- Anonymous share links (https://host/share/{token}) ---------------------------------
    // Addressed by the link's own origin, never baseUrl(): a share can point at any server and
    // must work with no pairing at all. The token rides in the path — no header, so the origin-
    // scoped AuthInterceptor is irrelevant to these calls.

    /** The share's playable tracklist + display metadata. [ApiException] 404 = revoked/unknown. */
    suspend fun fetchShare(link: ShareLink): SharePayload = withContext(Dispatchers.IO) {
        val request = Request.Builder().url(shareUrl(link)).get().build()
        execute(request) { json.decodeFromString<SharePayload>(it) }
    }

    fun shareStreamUrl(link: ShareLink, songId: Int): String = shareUrl(link, "/songs/$songId/stream")

    fun shareCoverUrl(link: ShareLink, songId: Int, size: Int): String =
        shareUrl(link, "/songs/$songId/cover?size=$size")

    suspend fun fetchShareLyrics(link: ShareLink, songId: Int): Lyrics = withContext(Dispatchers.IO) {
        val request = Request.Builder().url(shareUrl(link, "/songs/$songId/lyrics")).get().build()
        execute(request) { json.decodeFromString<LyricsResponse>(it).toLyrics() }
    }

    private fun shareUrl(link: ShareLink, path: String = ""): String =
        "${link.origin}$API_PREFIX/api/share/${URLEncoder.encode(link.token, "UTF-8")}$path"

    // --- Friend invites (https://host/invite/{token}) ----------------------------------------

    /** Who invited you and for which email — never consumes the single-use token. */
    suspend fun peekInvite(link: InviteLink): InvitePeek = withContext(Dispatchers.IO) {
        val request = Request.Builder()
            .url("${link.origin}$API_PREFIX/api/invite/${URLEncoder.encode(link.token, "UTF-8")}")
            .get()
            .build()
        execute(request) { json.decodeFromString<InvitePeek>(it) }
    }

    /**
     * Redeems the invite and returns the bearer this phone will pair with — the native-client
     * variant of the web's cookie-writing accept, consumed only on the explicit Accept tap.
     */
    suspend fun acceptInvite(link: InviteLink): String = withContext(Dispatchers.IO) {
        val body = json.encodeToString(AcceptInviteBody(link.token)).toRequestBody(JSON_MEDIA_TYPE)
        val request = Request.Builder()
            .url("${link.origin}$API_PREFIX/api/invite/accept-token")
            .post(body)
            .build()
        execute(request) { json.decodeFromString<AccessTokenResponse>(it).accessToken }
    }

    private suspend fun <T> get(path: String, parse: (String) -> T): T = withContext(Dispatchers.IO) {
        execute(Request.Builder().url(url(path)).get().build()) { parse(it) }
    }

    private suspend fun post(path: String) = withContext(Dispatchers.IO) {
        execute(Request.Builder().url(url(path)).post(EMPTY_BODY).build()) { }
    }

    private fun <T> execute(request: Request, parse: (String) -> T): T =
        client.newCall(request).execute().use { response ->
            // Only 401 means the token itself is dead. A 403 is an authorization answer about
            // THIS request (e.g. the server's friend/demo read-only middlewares) — treating it
            // as a revoked pairing used to silently unpair the phone over a single denied call.
            if (response.code == 401) throw UnauthorizedException()
            if (!response.isSuccessful) throw ApiException(response.code, "Request failed: ${response.code}")
            parse(response.body.string())
        }

    companion object {
        /** The frontend route that proxies to the API, header-for-header. */
        const val API_PREFIX = "/api/mh"
        private val EMPTY_BODY = ByteArray(0).toRequestBody(null)
        private val JSON_MEDIA_TYPE = "application/json; charset=utf-8".toMediaType()

        /** The passkey `begin` takes no parameters; OkHttp still needs a body for a POST. */
        private val EMPTY_JSON_BODY = "{}".toRequestBody(JSON_MEDIA_TYPE)
    }
}
