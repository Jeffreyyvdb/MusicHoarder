package com.musichoarder.app.data

import android.content.Context
import android.net.Uri
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking

/**
 * Where this phone is paired, and the bearer token that gets it in.
 *
 * [baseUrl] is the *frontend* origin, not the API's: the SvelteKit app proxies `/api/mh` to the
 * API and forwards the Authorization header, so a paired phone needs exactly one reachable host —
 * the same one the browser uses.
 *
 * [role] is the account's role as reported by `/api/auth/me` at pairing time. A `Friend` session
 * reads music through the grant-scoped `/api/shared` endpoints instead of the owner ones (see
 * [ApiRoutes]); null means owner behaviour — which also covers phones paired before roles existed.
 */
data class ServerSession(val baseUrl: String, val token: String, val role: String? = null) {
    val isFriend: Boolean get() = role.equals("Friend", ignoreCase = true)
}

private val Context.sessionDataStore: DataStore<Preferences> by preferencesDataStore(name = "server_session")

/**
 * Persists the pairing and exposes it synchronously.
 *
 * The synchronous part matters: the playback service builds its HTTP stack in `onCreate`, which the
 * system can call before any coroutine of ours has run, so the token has to be readable right then.
 * The store is app-private, the same protection every Android credential store starts from.
 */
class SessionStore(context: Context) {
    private val dataStore = context.applicationContext.sessionDataStore

    private val _session = MutableStateFlow<ServerSession?>(null)
    val session: StateFlow<ServerSession?> = _session.asStateFlow()

    init {
        _session.value = runBlocking { read(dataStore.data.first()) }
    }

    suspend fun save(session: ServerSession) {
        dataStore.edit { prefs ->
            prefs[KEY_BASE_URL] = session.baseUrl
            prefs[KEY_TOKEN] = session.token
            val role = session.role
            if (role.isNullOrBlank()) prefs.remove(KEY_ROLE) else prefs[KEY_ROLE] = role
        }
        _session.value = session
    }

    suspend fun clear() {
        dataStore.edit { it.clear() }
        _session.value = null
    }

    private fun read(prefs: Preferences): ServerSession? {
        val baseUrl = prefs[KEY_BASE_URL]
        val token = prefs[KEY_TOKEN]
        if (baseUrl.isNullOrBlank() || token.isNullOrBlank()) return null
        return ServerSession(baseUrl, token, role = prefs[KEY_ROLE])
    }

    private companion object {
        val KEY_BASE_URL = stringPreferencesKey("base_url")
        val KEY_TOKEN = stringPreferencesKey("token")
        val KEY_ROLE = stringPreferencesKey("role")
    }
}

/**
 * The `musichoarder://pair?v=1&url=…&token=…` payload the web UI renders as a QR code
 * (Settings → Account → Mobile app).
 */
object PairingUri {
    private const val SCHEME = "musichoarder"
    private const val HOST = "pair"

    /** Parses a scanned/pasted pairing code, or returns null when it is not one of ours. */
    fun parse(raw: String): ServerSession? {
        val uri = runCatching { Uri.parse(raw.trim()) }.getOrNull() ?: return null
        if (!SCHEME.equals(uri.scheme, ignoreCase = true) || !HOST.equals(uri.host, ignoreCase = true)) return null
        val url = uri.getQueryParameter("url")?.let(::normalizeBaseUrl) ?: return null
        val token = uri.getQueryParameter("token")?.trim().orEmpty()
        return if (token.isEmpty()) null else ServerSession(url, token)
    }

    /**
     * Accepts what someone would actually type ("musichoarder.app", with or without a trailing
     * slash) and returns an absolute origin, or null when it is not a usable http(s) URL.
     */
    fun normalizeBaseUrl(raw: String): String? {
        var value = raw.trim().trimEnd('/')
        if (value.isEmpty()) return null
        if (!value.startsWith("http://") && !value.startsWith("https://")) value = "https://$value"
        val uri = runCatching { Uri.parse(value) }.getOrNull() ?: return null
        if (uri.host.isNullOrBlank()) return null
        return value
    }
}

/**
 * The `musichoarder://auth?token=…&url=…` handoff the magic-link page offers when the sign-in was
 * requested from the app itself (PairScreen → email sign-in). Unlike a pairing code the token is
 * a one-time magic-link token, not a session — the app still has to exchange it at
 * `POST /api/auth/token` before it has anything worth persisting.
 */
object LoginLinkUri {
    private const val SCHEME = "musichoarder"
    private const val HOST = "auth"

    data class LoginLink(val baseUrl: String, val token: String)

    /** Parses an auth handoff link, or returns null when it is not one of ours. */
    fun parse(raw: String): LoginLink? {
        val uri = runCatching { Uri.parse(raw.trim()) }.getOrNull() ?: return null
        if (!SCHEME.equals(uri.scheme, ignoreCase = true) || !HOST.equals(uri.host, ignoreCase = true)) return null
        val url = uri.getQueryParameter("url")?.let(PairingUri::normalizeBaseUrl) ?: return null
        val token = uri.getQueryParameter("token")?.trim().orEmpty()
        return if (token.isEmpty()) null else LoginLink(url, token)
    }
}
