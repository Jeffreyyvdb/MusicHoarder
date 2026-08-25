package com.musichoarder.app.ui

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.musichoarder.app.MusicHoarderApp
import com.musichoarder.app.data.AccountsState
import com.musichoarder.app.data.Album
import com.musichoarder.app.data.AlbumSortKey
import com.musichoarder.app.data.ArtistMode
import com.musichoarder.app.data.ChipKey
import android.net.Uri
import com.musichoarder.app.data.ApiException
import com.musichoarder.app.data.LibraryContent
import com.musichoarder.app.data.LibraryTab
import com.musichoarder.app.data.LibraryUiState
import com.musichoarder.app.data.LoginLinkUri
import com.musichoarder.app.data.PairingUri
import com.musichoarder.app.data.SortKey
import com.musichoarder.app.data.StoredAccount
import com.musichoarder.app.data.UnauthorizedException
import com.musichoarder.app.data.ServerSession
import com.musichoarder.app.data.Track
import com.musichoarder.app.data.defaultAscending
import com.musichoarder.app.data.foldLibrary
import com.musichoarder.app.data.likedNow
import com.musichoarder.app.data.resolveAlbum
import com.musichoarder.app.data.sortForChipChange
import com.musichoarder.app.player.PlayerController
import com.musichoarder.app.player.VideoController
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.flowOn
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.mapLatest
import kotlinx.coroutines.flow.merge
import kotlinx.coroutines.flow.shareIn
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlin.random.Random

@OptIn(ExperimentalCoroutinesApi::class)
class AppViewModel(application: Application) : AndroidViewModel(application) {
    private val graph = (application as MusicHoarderApp).graph

    val session: StateFlow<ServerSession?> = graph.sessions.session

    /** Every account remembered on this phone, for the switcher in the library top bar. */
    val accounts: StateFlow<AccountsState> = graph.sessions.accounts
    val library = graph.library.state

    /** Account-switcher events (eviction fallbacks) that deserve the same snackbar treatment. */
    private val _localMessages = MutableSharedFlow<String>(extraBufferCapacity = 4)

    /** Failures the library reports that are worth a snackbar rather than a whole error screen. */
    val messages: SharedFlow<String> = merge(graph.library.messages, _localMessages)
        .shareIn(viewModelScope, SharingStarted.WhileSubscribed(5_000))

    /** Optimistic heart state, read by every row that draws one. */
    val likes = graph.library.likes

    /** Provider-link status per album, keyed `artistLower::albumLower`. */
    val albumStatuses = graph.library.albumStatuses

    private val _ui = MutableStateFlow(LibraryUiState(seed = Random.nextInt().toString(radix = 36)))
    val ui: StateFlow<LibraryUiState> = _ui.asStateFlow()

    /**
     * The lists the four tabs render.
     *
     * `mapLatest` drops an in-flight fold the moment the next keystroke arrives, and `flowOn` keeps
     * the whole thing off the main thread - grouping and sorting four thousand tracks is not frame
     * work. Nothing here belongs in `derivedStateOf`, which would run it on the composition.
     */
    val content: StateFlow<LibraryContent> = combine(
        graph.library.state,
        _ui,
        graph.library.likes,
        graph.library.plays,
    ) { state, ui, likes, plays -> Fold(state, ui, likes, plays) }
        .mapLatest { foldLibrary(it.state, it.ui, it.likes, it.plays) }
        .flowOn(Dispatchers.Default)
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), LibraryContent())

    private data class Fold(
        val state: com.musichoarder.app.data.LibraryState,
        val ui: LibraryUiState,
        val likes: Map<Int, String?>,
        val plays: Map<Int, com.musichoarder.app.data.PlayStat>,
    )

    /** The album the drilldown is showing, resolved against the unscoped list. */
    val openAlbum: StateFlow<Album?> = combine(graph.library.state, _ui) { state, ui ->
        resolveAlbum(state.albums, ui.openAlbumKey)
    }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), null)

    private val _pairError = MutableStateFlow<String?>(null)
    val pairError: StateFlow<String?> = _pairError.asStateFlow()

    /** A deep-linked pairing/sign-in link waiting on confirmation, because a session already exists. */
    private val _pendingPairingLink = MutableStateFlow<String?>(null)
    val pendingPairingHost: StateFlow<String?> = _pendingPairingLink
        .map { raw -> raw?.let(::linkedBaseUrl) }
        .stateIn(viewModelScope, SharingStarted.Eagerly, null)

    /** The email a sign-in link was just sent to; the pair screen shows the "check your email" state. */
    private val _emailLinkSentTo = MutableStateFlow<String?>(null)
    val emailLinkSentTo: StateFlow<String?> = _emailLinkSentTo.asStateFlow()

    val player = PlayerController(
        context = application,
        api = graph.api,
        scope = viewModelScope,
        onTrackStarted = { songId ->
            // Mirror what the server is about to record, so the Overview's "Last played" and
            // "Discover" shelves move as you listen instead of waiting for the next full fetch.
            graph.library.notePlayed(songId, System.currentTimeMillis())
            viewModelScope.launch { graph.api.reportPlayed(songId) }
        },
    )

    /** The muted clip behind the player; it chases [player]'s clock and never drives it. */
    val video = VideoController(
        context = application,
        api = graph.api,
        httpClient = graph.httpClient,
        scope = viewModelScope,
    )

    private val _lyrics = MutableStateFlow<LyricsUiState>(LyricsUiState.Loading)
    val lyrics: StateFlow<LyricsUiState> = _lyrics.asStateFlow()

    /**
     * The hearted songs, as `GET /songs` reported them plus anything toggled since — the boolean
     * view of the like overlay, for callers that do not need the timestamp (the player's heart).
     * The library lists read the overlay directly, because their "liked" sort orders on the date.
     */
    val likedIds: StateFlow<Set<Int>> =
        combine(graph.library.state, graph.library.likes) { state, likes ->
            state.trackListBase.filterTo(mutableListOf()) { likedNow(likes, it) }.mapTo(HashSet()) { it.id }
        }
            .flowOn(Dispatchers.Default)
            .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), emptySet())

    /** The player's heart, which knows a song id rather than the row it came from. */
    fun toggleLike(songId: Int) {
        val track = graph.library.trackById(songId) ?: return
        toggleLike(track)
    }

    /**
     * Playback speed. The clip has to move with it: it chases the audio clock and hard-seeks past
     * 300 ms of drift, so left at 1x behind a 1.5x song it would be re-seeking on every tick.
     */
    fun setPlaybackSpeed(rate: Float) {
        player.setPlaybackSpeed(rate)
        video.setSpeed(rate)
    }

    private var lyricsSongId: Int? = null
    private var lyricsJob: Job? = null

    /**
     * Loads the lyrics and looks up the video for [songId]. Both are per-song extras the library
     * dump does not carry, so they are fetched when the player actually shows a track.
     */
    fun onNowPlayingTrackChanged(songId: Int?) {
        video.load(songId)
        if (songId == lyricsSongId) return
        lyricsSongId = songId
        lyricsJob?.cancel()
        _lyrics.value = LyricsUiState.Loading
        if (songId == null) return
        lyricsJob = viewModelScope.launch {
            _lyrics.value = try {
                LyricsUiState.Ready(graph.api.fetchLyrics(songId))
            } catch (e: Exception) {
                LyricsUiState.Failed(e.message ?: "Could not load lyrics.")
            }
        }
    }

    init {
        if (session.value != null) start()

        // A revoked or expired device session evicts only the account it belongs to: with another
        // account remembered the app falls back to it, and only losing the last one lands the user
        // back on the pairing screen with an explanation — not on an empty library that silently
        // never loads.
        viewModelScope.launch {
            graph.library.state.collect { state ->
                if (state.isPairingRevoked) {
                    evictActiveAccount(state.error)
                }
            }
        }
    }

    private fun start() {
        player.connect()
        viewModelScope.launch { graph.library.refresh() }
    }

    fun refresh() {
        viewModelScope.launch { graph.library.refresh(force = true) }
    }

    /**
     * A `musichoarder://pair` or `musichoarder://auth` link opened from outside the app.
     *
     * On first run this just pairs (or finishes the email sign-in). When the app is already paired
     * it asks first: a link can be handed over by anything — a web page, a message — and silently
     * re-pointing someone's library at another server on a single tap is not a thing a link should
     * be able to do.
     */
    fun onAppLink(raw: String) {
        if (_pendingPairingLink.value == raw) return
        if (linkedBaseUrl(raw) == null) {
            reportPairProblem("That link is not a MusicHoarder pairing code or sign-in link.")
            return
        }
        if (session.value == null) applyAppLink(raw) else _pendingPairingLink.value = raw
    }

    /** Confirms a link that would re-point an already-paired app. */
    fun confirmPendingPairingLink() {
        val raw = _pendingPairingLink.value ?: return
        _pendingPairingLink.value = null
        applyAppLink(raw)
    }

    private fun applyAppLink(raw: String) {
        PairingUri.parse(raw)?.let { pair(it); return }
        LoginLinkUri.parse(raw)?.let { loginWithToken(it.baseUrl, it.token) }
    }

    private fun linkedBaseUrl(raw: String): String? =
        PairingUri.parse(raw)?.baseUrl ?: LoginLinkUri.parse(raw)?.baseUrl

    fun dismissPendingPairingLink() {
        _pendingPairingLink.value = null
    }

    /** Accepts a scanned QR payload, or a URL + token typed by hand. */
    fun pairFromCode(raw: String) {
        val parsed = PairingUri.parse(raw)
        if (parsed == null) {
            reportPairProblem("That code is not a MusicHoarder pairing code.")
            return
        }
        pair(parsed)
    }

    /**
     * Pairing problems land where the user is: the pairing screen's error pane when unpaired, a
     * snackbar when the scan came from the account menu of an already-paired app (the pairing
     * screen — and its error pane — is not on screen then).
     */
    fun reportPairProblem(message: String) {
        if (session.value != null) _localMessages.tryEmit(message) else _pairError.value = message
    }

    /**
     * The no-PC path: asks the server to email a one-time sign-in link. Tapping the link on this
     * phone bounces through the browser handoff page into [onAppLink] as a `musichoarder://auth`
     * deep link, and [loginWithToken] finishes the exchange.
     */
    fun requestEmailLink(baseUrl: String, email: String) {
        val normalized = PairingUri.normalizeBaseUrl(baseUrl)
        if (normalized == null) {
            _pairError.value = "Enter the server address, for example https://musichoarder.app"
            return
        }
        val trimmed = email.trim()
        if (trimmed.isEmpty() || '@' !in trimmed) {
            _pairError.value = "Enter the email address of your MusicHoarder account."
            return
        }
        _pairError.value = null
        viewModelScope.launch {
            val result = runCatching { graph.api.requestLoginLink(normalized, trimmed) }
            val response = result.getOrNull()
            if (response == null) {
                _pairError.value = "Could not reach $normalized."
                return@launch
            }
            // A Development server hands the link straight back instead of emailing it; skip the
            // inbox round-trip and sign in with it now.
            val devToken = response.magicLinkUrl
                ?.let { runCatching { Uri.parse(it).getQueryParameter("token") }.getOrNull() }
            if (devToken != null) loginWithToken(normalized, devToken)
            else _emailLinkSentTo.value = trimmed
        }
    }

    /** Exchanges a magic-link token for a bearer session, then pairs with it as usual. */
    private fun loginWithToken(baseUrl: String, token: String) {
        _pairError.value = null
        viewModelScope.launch {
            val exchange = runCatching { graph.api.exchangeLoginToken(baseUrl, token) }
            val bearer = exchange.getOrNull()
            if (bearer == null) {
                _pairError.value = if (exchange.exceptionOrNull() is ApiException) {
                    "That sign-in link has expired or was already used. Request a new one."
                } else {
                    "Could not reach $baseUrl. The current pairing is untouched."
                }
                return@launch
            }
            _emailLinkSentTo.value = null
            pair(ServerSession(baseUrl, bearer))
        }
    }

    fun pairManually(baseUrl: String, token: String) {
        val normalized = PairingUri.normalizeBaseUrl(baseUrl)
        if (normalized == null) {
            _pairError.value = "Enter the server address, for example https://musichoarder.app"
            return
        }
        if (token.isBlank()) {
            _pairError.value = "Paste the access token from the pairing screen."
            return
        }
        pair(ServerSession(normalized, token.trim()))
    }

    private fun pair(newSession: ServerSession) {
        _pairError.value = null
        viewModelScope.launch {
            // Probe the candidate *before* persisting it. Saving first and rolling back on failure
            // meant a stale code — or simply scanning while offline, which is indistinguishable from
            // a rejected token here — wiped a pairing that was working perfectly well.
            val probe = runCatching { graph.api.fetchMe(newSession) }
            val me = probe.getOrNull()
            if (me == null) {
                reportPairProblem(
                    if (probe.exceptionOrNull() is UnauthorizedException) {
                        "The server did not accept that pairing code. Try a fresh one."
                    } else {
                        "Could not reach ${newSession.baseUrl}. The current pairing is untouched."
                    },
                )
                return@launch
            }
            // Keep the probe's answer: the role decides which endpoints this pairing reads
            // (a Friend session streams through /api/shared — see ApiRoutes), and the identity
            // fields label the account switcher. Captured here, before start() fires the first
            // library fetch. addAccount dedupes, so re-scanning a QR for a known account just
            // renews its token; a new account is added alongside the current one and made active.
            graph.sessions.addAccount(
                StoredAccount(
                    baseUrl = newSession.baseUrl,
                    token = newSession.token,
                    role = me.role,
                    userId = me.id,
                    email = me.email,
                    displayName = me.displayName,
                ),
            )
            // Activating a different pairing means everything held from the last one has to go:
            // the library cache is not refetched while it still holds rows (`refresh` no-ops
            // unless forced), and a queued MediaItem keeps the absolute stream URL it was built
            // with, so playback would carry on against the old host (or the old account).
            player.stop()
            video.load(null)
            graph.library.clear()
            start()
        }
    }

    /** Activates the remembered account at [index]; a stale index is ignored. */
    fun switchAccount(index: Int) {
        val current = accounts.value
        if (index == current.activeIndex || current.accounts.getOrNull(index) == null) return
        viewModelScope.launch {
            graph.sessions.switchTo(index) ?: return@launch
            // Same teardown as pairing: stop the old account's playback and drop its library.
            player.stop()
            video.load(null)
            graph.library.clear()
            start()
            // Opportunistic identity refresh: the switch runs on the stored role (instant, works
            // offline); if the server disagrees — role changed, or the session died — this
            // corrects it. A 401 flows through the library's isPairingRevoked path and evicts
            // just this account.
            runCatching { graph.api.fetchMe() }.getOrNull()?.let { me ->
                val stored = graph.sessions.accounts.value.active
                if (stored != null && stored.role != me.role) {
                    graph.sessions.updateActive(me.role, me.id, me.email, me.displayName)
                    graph.library.refresh(force = true)
                }
            }
        }
    }

    fun setPairError(message: String?) {
        _pairError.value = message
    }

    /** Signs the active account out of this phone, falling back to the next remembered one. */
    fun unpair() {
        _emailLinkSentTo.value = null
        evictActiveAccount(error = null)
    }

    /**
     * Drops the active account (revoked session, or an explicit sign-out) and falls back to the
     * next remembered account when there is one; only losing the last account tears the player
     * down and returns to the pairing screen.
     */
    private fun evictActiveAccount(error: String?) {
        viewModelScope.launch {
            val state = graph.sessions.accounts.value
            val evicted = state.active
            graph.sessions.removeAccount(state.activeIndex)

            video.load(null)
            player.stop()
            graph.library.clear()

            val next = graph.sessions.accounts.value.active
            if (next != null) {
                start()
                _localMessages.tryEmit(
                    if (evicted != null) "Signed out of ${evicted.label} — now using ${next.label}"
                    else "Now using ${next.label}",
                )
            } else {
                player.release()
                graph.sessions.clear()
                _pairError.value = error
            }
        }
    }

    // ---- Library view state ------------------------------------------------------------------
    // Every mutation goes through a named method so the state stays one object and the back handler
    // has a single place to read.

    fun selectTab(tab: LibraryTab) = _ui.update { it.copy(tab = tab) }

    fun setQuery(query: String) = _ui.update { it.copy(query = query) }

    fun toggleChip(key: ChipKey) = _ui.update { state ->
        val next = if (key in state.chips) state.chips - key else state.chips + key
        val (sortKey, ascending) = sortForChipChange(state.chips, next, state.sortKey, state.sortAscending)
        state.copy(chips = next, sortKey = sortKey, sortAscending = ascending)
    }

    fun clearChips() = _ui.update { state ->
        val (sortKey, ascending) = sortForChipChange(state.chips, emptySet(), state.sortKey, state.sortAscending)
        state.copy(chips = emptySet(), sortKey = sortKey, sortAscending = ascending)
    }

    /** Picking the current key again flips the direction, as the web's column headers do. */
    fun setSort(key: SortKey) = _ui.update { state ->
        if (state.sortKey == key) state.copy(sortAscending = !state.sortAscending)
        else state.copy(sortKey = key, sortAscending = defaultAscending(key))
    }

    fun setAlbumSort(key: AlbumSortKey) = _ui.update { it.copy(albumSort = key) }

    fun toggleUnreleasedOnly() = _ui.update { it.copy(unreleasedOnly = !it.unreleasedOnly) }

    fun setArtistMode(mode: ArtistMode) = _ui.update { it.copy(artistMode = mode) }

    fun setLetter(letter: String?) = _ui.update { it.copy(letter = letter) }

    /** Tapping an artist narrows the Albums tab in place, the way the web's `?artist=` link does. */
    fun openArtist(name: String) =
        _ui.update { it.copy(artistFilter = name, tab = LibraryTab.Albums, letter = null) }

    fun clearArtistFilter() = _ui.update { it.copy(artistFilter = null) }

    fun openAlbum(album: Album) = _ui.update { it.copy(openAlbumKey = album.key) }

    fun closeAlbum() = _ui.update { it.copy(openAlbumKey = null) }

    /** Loads the album grid's link-status dots for what is currently on screen. */
    fun ensureAlbumStatuses(albums: List<Album>) =
        graph.library.loadAlbumStatuses(albums, viewModelScope)

    fun toggleLike(track: Track) {
        viewModelScope.launch { graph.library.toggleLike(track) }
    }

    /** The artist portrait endpoint. 404s are common and simply never paint. */
    fun artistImageUrl(name: String): String = graph.api.artistImageUrl(name)

    fun play(tracks: List<Track>, startIndex: Int) = player.play(tracks, startIndex)

    /** Null when there is nothing to show — callers fall back to a letter tile. */
    fun coverUrl(trackId: Int?, hasCover: Boolean, size: Int): String? =
        if (trackId != null && hasCover) graph.api.coverUrl(trackId, size) else null

    override fun onCleared() {
        player.release()
        video.release()
        super.onCleared()
    }
}
