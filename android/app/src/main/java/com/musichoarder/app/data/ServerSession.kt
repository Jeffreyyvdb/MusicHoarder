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
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

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

/**
 * One remembered pairing. The phone can hold several — e.g. an owner and a friend account, or the
 * same account on two servers — and switch between them; only the active one talks to the network
 * (via [toSession]). Identity fields come from `/api/auth/me` at pairing time and label the
 * account switcher.
 */
@Serializable
data class StoredAccount(
    val baseUrl: String,
    val token: String,
    val role: String? = null,
    val userId: String? = null,
    val email: String? = null,
    val displayName: String? = null,
) {
    fun toSession() = ServerSession(baseUrl, token, role)

    /** What the switcher shows for this account. (No android.net.Uri here — JVM tests use this.) */
    val label: String
        get() = displayName ?: email
            ?: baseUrl.removePrefix("https://").removePrefix("http://").substringBefore('/')

    /**
     * Same account on the same server? Keyed by `(baseUrl, userId)`, falling back to email for
     * pre-userId rows, then to the token itself when identity fields are absent.
     */
    fun matches(other: StoredAccount): Boolean {
        if (baseUrl != other.baseUrl) return false
        if (userId != null && other.userId != null) return userId == other.userId
        if (email != null && other.email != null) return email == other.email
        return token == other.token
    }
}

/** The full account list plus which one is active. Immutable; mutations return a new state. */
@Serializable
data class AccountsState(
    val accounts: List<StoredAccount> = emptyList(),
    val activeIndex: Int = -1,
) {
    val active: StoredAccount? get() = accounts.getOrNull(activeIndex)

    /**
     * Adds (or refreshes) [account]: an entry for the same account is replaced in place — so
     * re-scanning a QR just renews the token — otherwise the account is appended. Made active
     * unless [makeActive] is false.
     */
    fun adding(account: StoredAccount, makeActive: Boolean = true): AccountsState {
        val existing = accounts.indexOfFirst { it.matches(account) }
        val list = if (existing >= 0) {
            accounts.toMutableList().also { it[existing] = account }
        } else {
            accounts + account
        }
        val index = if (existing >= 0) existing else list.lastIndex
        return AccountsState(list, if (makeActive || activeIndex !in list.indices) index else activeIndex)
    }

    fun switchedTo(index: Int): AccountsState =
        if (index in accounts.indices) copy(activeIndex = index) else this

    /** Removes one account; removing the active one promotes the next remembered. */
    fun removingAt(index: Int): AccountsState {
        if (index !in accounts.indices) return this
        val list = accounts.toMutableList().also { it.removeAt(index) }
        if (list.isEmpty()) return AccountsState()
        val nextActive = when {
            index < activeIndex -> activeIndex - 1
            index == activeIndex -> activeIndex.coerceAtMost(list.lastIndex)
            else -> activeIndex
        }
        return AccountsState(list, nextActive.coerceIn(0, list.lastIndex))
    }

    /** Refreshes the active account's identity fields after a `/api/auth/me` probe. */
    fun updatingActive(role: String?, userId: String?, email: String?, displayName: String?): AccountsState {
        val current = active ?: return this
        val updated = current.copy(role = role, userId = userId, email = email, displayName = displayName)
        return copy(accounts = accounts.toMutableList().also { it[activeIndex] = updated })
    }
}

private val accountsJson = Json { ignoreUnknownKeys = true }

/** Decodes a persisted [AccountsState]; null/corrupt input reads as empty. */
internal fun decodeAccountsState(raw: String?): AccountsState {
    if (raw.isNullOrBlank()) return AccountsState()
    val state = runCatching { accountsJson.decodeFromString<AccountsState>(raw) }.getOrNull()
        ?: return AccountsState()
    val accounts = state.accounts.filter { it.baseUrl.isNotBlank() && it.token.isNotBlank() }
    if (accounts.isEmpty()) return AccountsState()
    val index = if (state.activeIndex in accounts.indices) state.activeIndex else 0
    return AccountsState(accounts, index)
}

internal fun encodeAccountsState(state: AccountsState): String = accountsJson.encodeToString(state)

/** Folds the pre-multi-account single-pairing keys into a one-entry state, or null when absent. */
internal fun migrateLegacyAccount(baseUrl: String?, token: String?, role: String?): AccountsState? {
    if (baseUrl.isNullOrBlank() || token.isNullOrBlank()) return null
    return AccountsState(listOf(StoredAccount(baseUrl, token, role = role)), 0)
}

private val Context.sessionDataStore: DataStore<Preferences> by preferencesDataStore(name = "server_session")

/**
 * Persists the remembered accounts and exposes the active pairing synchronously.
 *
 * The synchronous part matters: the playback service builds its HTTP stack in `onCreate`, which the
 * system can call before any coroutine of ours has run, so the token has to be readable right then.
 * That is why the whole account list lives under a single string key ([KEY_ACCOUNTS]) — the init
 * read stays one blocking preference fetch. The store is app-private, the same protection every
 * Android credential store starts from.
 */
class SessionStore(context: Context) {
    private val dataStore = context.applicationContext.sessionDataStore

    private val _accounts = MutableStateFlow(AccountsState())
    val accounts: StateFlow<AccountsState> = _accounts.asStateFlow()

    private val _session = MutableStateFlow<ServerSession?>(null)
    val session: StateFlow<ServerSession?> = _session.asStateFlow()

    init {
        val state = runBlocking { read(dataStore.data.first()) }
        _accounts.value = state
        _session.value = state.active?.toSession()
    }

    /** Adds or refreshes an account (see [AccountsState.adding]) and makes it the active one. */
    suspend fun addAccount(account: StoredAccount) = persist(_accounts.value.adding(account))

    /** Makes the account at [index] active; returns it, or null when the index is stale. */
    suspend fun switchTo(index: Int): StoredAccount? {
        val next = _accounts.value.switchedTo(index)
        persist(next)
        return next.accounts.getOrNull(index)
    }

    /** Forgets one account; removing the active one falls back to the next remembered. */
    suspend fun removeAccount(index: Int) = persist(_accounts.value.removingAt(index))

    /** Refreshes the active account's identity after a `/api/auth/me` probe. */
    suspend fun updateActive(role: String?, userId: String?, email: String?, displayName: String?) =
        persist(_accounts.value.updatingActive(role, userId, email, displayName))

    suspend fun clear() {
        dataStore.edit { it.clear() }
        _accounts.value = AccountsState()
        _session.value = null
    }

    private suspend fun persist(state: AccountsState) {
        dataStore.edit { prefs ->
            if (state.accounts.isEmpty()) {
                prefs.remove(KEY_ACCOUNTS)
            } else {
                prefs[KEY_ACCOUNTS] = encodeAccountsState(state)
            }
            // The legacy single-pairing keys are superseded the first time anything persists.
            prefs.remove(KEY_BASE_URL)
            prefs.remove(KEY_TOKEN)
            prefs.remove(KEY_ROLE)
        }
        _accounts.value = state
        _session.value = state.active?.toSession()
    }

    private fun read(prefs: Preferences): AccountsState {
        prefs[KEY_ACCOUNTS]?.let { return decodeAccountsState(it) }
        return migrateLegacyAccount(prefs[KEY_BASE_URL], prefs[KEY_TOKEN], prefs[KEY_ROLE])
            ?: AccountsState()
    }

    private companion object {
        val KEY_ACCOUNTS = stringPreferencesKey("accounts_json")
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
