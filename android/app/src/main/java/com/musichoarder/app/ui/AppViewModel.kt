package com.musichoarder.app.ui

import android.app.Application
import android.content.Context
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
import com.musichoarder.app.data.InviteLink
import com.musichoarder.app.data.LibraryUiState
import com.musichoarder.app.data.NowPlayingLinks
import com.musichoarder.app.data.LoginLinkUri
import com.musichoarder.app.data.PairingUri
import com.musichoarder.app.data.PasskeyCancelledException
import com.musichoarder.app.data.PasskeySignIn
import com.musichoarder.app.data.PasskeyUnavailableException
import com.musichoarder.app.data.ShareLink
import com.musichoarder.app.data.SortKey
import com.musichoarder.app.data.StoredAccount
import com.musichoarder.app.data.UnauthorizedException
import com.musichoarder.app.data.ServerSession
import com.musichoarder.app.data.Track
import com.musichoarder.app.data.toTrack
import com.musichoarder.app.data.defaultAscending
import com.musichoarder.app.data.foldLibrary
import com.musichoarder.app.data.likedNow
import com.musichoarder.app.data.resolveAlbum
import com.musichoarder.app.data.resolveNowPlayingLinks
import com.musichoarder.app.data.sortForChipChange
import com.musichoarder.app.player.PlayerController
import com.musichoarder.app.player.VideoController
import kotlinx.coroutines.CancellationException
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
import kotlinx.coroutines.flow.distinctUntilChanged
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

    /**
     * True while the account switcher's "Add account" is showing the sign-in screen over an
     * already-signed-in app. It is the phone's equivalent of the web's `/login?switch`: the same
     * screen, every sign-in option, and the current account left untouched until a new one lands.
     */
    private val _addingAccount = MutableStateFlow(false)
    val addingAccount: StateFlow<Boolean> = _addingAccount.asStateFlow()

    val player = PlayerController(
        context = application,
        api = graph.api,
        scope = viewModelScope,
        onTrackStarted = { songId ->
            // A share queue's ids belong to the sharing server — feeding them into the local
            // play stats (or reporting them against the paired one) would corrupt both.
            if (!_isShareQueue.value) {
                // Mirror what the server is about to record, so the Overview's "Last played" and
                // "Discover" shelves move as you listen instead of waiting for the next full fetch.
                graph.library.notePlayed(songId, System.currentTimeMillis())
                viewModelScope.launch { graph.api.reportPlayed(songId) }
            }
        },
        radioTracks = { seedId, exclude ->
            // No station for a share queue: those ids belong to the sharing server, so asking the
            // paired one about them would answer about somebody else's songs entirely.
            if (_isShareQueue.value) {
                emptyList()
            } else {
                graph.api.fetchRadio(seedId, exclude).mapNotNull(graph.library::trackById)
            }
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
        // Share queues: no video backdrop (the controller is wired to the paired api), and lyrics
        // come from the share's own anonymous endpoint on the sharing server.
        val shareLink = if (_isShareQueue.value) _share.value?.link else null
        if (shareLink == null) video.load(songId) else video.load(null)
        if (songId == lyricsSongId) return
        lyricsSongId = songId
        lyricsJob?.cancel()
        _lyrics.value = LyricsUiState.Loading
        if (songId == null) return
        lyricsJob = viewModelScope.launch {
            _lyrics.value = try {
                LyricsUiState.Ready(
                    if (shareLink != null) graph.api.fetchShareLyrics(shareLink, songId)
                    else graph.api.fetchLyrics(songId)
                )
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
        refreshIdentity()
    }

    fun refresh() {
        viewModelScope.launch { graph.library.refresh(force = true) }
        refreshIdentity()
    }

    /**
     * What this account is currently allowed to do, by capability name.
     *
     * Re-read from the server rather than captured at pairing time. An admin turning a capability
     * off would otherwise have no effect on an already-paired phone until it re-paired, which is
     * not a thing anyone would think to do.
     */
    private val _capabilities = MutableStateFlow<Set<String>>(emptySet())
    val capabilities: StateFlow<Set<String>> = _capabilities.asStateFlow()

    fun can(capability: String): Boolean = _capabilities.value.contains(capability)

    /** Server capability name. Must match `Capability.TrackListening` on the API. */
    private val CAPABILITY_TRACK_LISTENING = "TrackListening"

    /**
     * Re-read identity and capabilities. Safe to call often — it is one small request.
     *
     * A failure deliberately leaves the last-known values in place: a flaky network must not make
     * the like button vanish. Only a genuine 401 clears them, and that path already unpairs.
     */
    fun refreshIdentity() {
        val issuedFor = graph.sessions.session.value ?: return
        viewModelScope.launch {
            val me = runCatching { graph.api.fetchMe() }.getOrNull() ?: return@launch

            // Drop the answer if the active account changed while it was in flight. This runs in
            // viewModelScope, which survives the account switch that restarted the caller, so a
            // slow /auth/me issued for account A can land after a switch to B — and without this
            // check it would write A's role, id and email onto B's stored account and hand B
            // A's capabilities.
            if (graph.sessions.session.value?.token != issuedFor.token) return@launch

            _capabilities.value = me.capabilities.toSet()
            val stored = graph.sessions.accounts.value.active
            if (stored != null && stored.role != me.role) {
                graph.sessions.updateActive(me.role, me.id, me.email, me.displayName)
            }
        }
    }

    // ---- Deep links --------------------------------------------------------------------------

    /**
     * Any link the system handed the app: an https share/invite App Link, a `musichoarder://pair`
     * code, or the `musichoarder://auth` sign-in handoff from the browser.
     *
     * Share and invite links open their own surfaces without touching the pairing. For the
     * custom-scheme links, first run just pairs (or finishes the email sign-in); when the app is
     * already paired it asks first — a link can be handed over by anything, and silently
     * re-pointing someone's library at another server on a single tap is not a thing a link
     * should be able to do. The exception is the sign-in screen being open for "Add account":
     * the link is then the answer to a request made on that screen seconds earlier, so asking
     * again buys nothing — and the QR button right next to it already pairs without a prompt.
     * Https links that match neither token grammar are dropped silently: routing a stray link to
     * the website into the pairing flow would show a misleading error.
     */
    fun onAppLink(raw: String) {
        val trimmed = raw.trim()
        ShareLink.parse(trimmed)?.let { openShare(it); return }
        InviteLink.parse(trimmed)?.let { openInvite(it); return }
        if (trimmed.startsWith("http://", ignoreCase = true) ||
            trimmed.startsWith("https://", ignoreCase = true)
        ) {
            return
        }
        if (_pendingPairingLink.value == trimmed) return
        if (linkedBaseUrl(trimmed) == null) {
            reportPairProblem("That link is not a MusicHoarder pairing code or sign-in link.")
            return
        }
        val expected = session.value == null || _addingAccount.value
        if (expected) applyAppLink(trimmed) else _pendingPairingLink.value = trimmed
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
     * Pairing problems land where the user is: the sign-in screen's error pane whenever that
     * screen is up — first run or "Add account" — and a snackbar otherwise, for the deep links
     * that arrive at a running library with no error pane anywhere on screen.
     */
    fun reportPairProblem(message: String) {
        val pairScreenShowing = session.value == null || _addingAccount.value
        if (pairScreenShowing) _pairError.value = message else _localMessages.tryEmit(message)
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

    /**
     * The other no-PC path: signs in with the passkey enrolled in the browser.
     *
     * Ends where the magic link does — [pair] with a proven bearer — but needs no inbox and no
     * round-trip out of the app. The passkey belongs to the web origin, so Android only offers it
     * once that origin's `/.well-known/assetlinks.json` vouches for this app; [PasskeySignIn]
     * turns that (very likely) first-run miss into an explanation rather than a bare failure.
     *
     * [activityContext] has to be the Activity — the system draws the passkey sheet over it.
     */
    fun signInWithPasskey(activityContext: Context, baseUrl: String) {
        val normalized = PairingUri.normalizeBaseUrl(baseUrl)
        if (normalized == null) {
            _pairError.value = "Enter the server address, for example https://musichoarder.app"
            return
        }
        _pairError.value = null
        viewModelScope.launch {
            try {
                val challenge = graph.api.beginPasskeySignIn(normalized)
                val assertion = PasskeySignIn.authenticate(activityContext, challenge.requestJson)
                val bearer = graph.api.completePasskeySignIn(normalized, challenge.state, assertion)
                _emailLinkSentTo.value = null
                pair(ServerSession(normalized, bearer))
            } catch (e: PasskeyCancelledException) {
                // Backing out of the system sheet is a decision, not a failure. Saying nothing is
                // the whole point — an error pane here would read as if the passkey were broken.
            } catch (e: PasskeyUnavailableException) {
                _pairError.value = e.message
            } catch (e: ApiException) {
                // 400 is the server's answer for an expired challenge or an assertion it could not
                // verify; both are worth another try rather than a dead end.
                _pairError.value = if (e.status == 400) {
                    "The server did not accept that passkey. Try again."
                } else {
                    "Could not reach $normalized."
                }
            } catch (e: CancellationException) {
                // The scope going away is not a sign-in failure, and swallowing it here would
                // leave a stale error on a screen that is being torn down anyway.
                throw e
            } catch (e: Exception) {
                _pairError.value = "Could not reach $normalized."
            }
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
            // Keep the probe's answer: the identity fields label the account switcher, and the
            // role rides along for the same reason (it no longer selects endpoints — the server
            // scopes them to the caller). Capabilities are NOT frozen here; refreshIdentity()
            // re-reads them on every foreground, so an admin's change reaches the phone without a
            // re-pair. addAccount dedupes, so re-scanning a QR for a known account just renews its
            // token; a new account is added alongside the current one and made active.
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
            _isShareQueue.value = false
            player.stop()
            video.load(null)
            graph.library.clear()
            _emailLinkSentTo.value = null
            _addingAccount.value = false
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

    /**
     * Opens the full sign-in screen over the signed-in app, so a second account can be added by
     * email, passkey, QR or token — not the QR scanner alone, which was every option the switcher
     * used to reach. Starts clean: the last entrance's error and "check your email" state belong
     * to the account that is already in.
     */
    fun beginAddAccount() {
        _pairError.value = null
        _emailLinkSentTo.value = null
        _addingAccount.value = true
    }

    /** Backs out of "Add account"; the active account was never touched. */
    fun cancelAddAccount() {
        _pairError.value = null
        _emailLinkSentTo.value = null
        _addingAccount.value = false
    }

    // ---- Anonymous share viewer --------------------------------------------------------------
    // Works with or without a pairing: the link's token is the whole capability, and every URL is
    // absolute against the link's own origin.

    private val _share = MutableStateFlow<ShareUiState?>(null)
    val share: StateFlow<ShareUiState?> = _share.asStateFlow()

    /**
     * True while the player's queue came from the share viewer. A share's ids belong to the
     * sharing server, so the flag keeps them out of play stats, likes, and the paired lyrics
     * and video routes.
     */
    private val _isShareQueue = MutableStateFlow(false)
    val isShareQueue: StateFlow<Boolean> = _isShareQueue.asStateFlow()

    /**
     * Where the player's `artist · album` line can navigate to, or null when nothing it links to is
     * reachable — a share queue's foreign ids, or a row the grids do not cover. Keyed on the track
     * id alone: the player pushes a state every 200 ms for the scrubber, and re-scanning the
     * library at that rate to answer a question that only changes between songs would be waste.
     */
    val nowPlayingLinks: StateFlow<NowPlayingLinks?> = combine(
        player.state.map { it.trackId }.distinctUntilChanged(),
        graph.library.state,
        _isShareQueue,
    ) { trackId, state, isShareQueue ->
        // A share track can carry a library track's id, so the flag has to rule it out before the
        // lookup — otherwise a colliding id would link the wrong record.
        if (isShareQueue) null else resolveNowPlayingLinks(state, trackId)
    }
        .flowOn(Dispatchers.Default)
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), null)

    private var shareJob: Job? = null

    fun openShare(link: ShareLink) {
        shareJob?.cancel()
        _share.value = ShareUiState.Loading(link)
        shareJob = viewModelScope.launch {
            _share.value = try {
                val payload = graph.api.fetchShare(link)
                val tracks = payload.tracks.map { dto ->
                    dto.toTrack(
                        album = payload.album,
                        streamUrl = graph.api.shareStreamUrl(link, dto.id),
                        artworkUrl = if (dto.hasCoverArt) graph.api.shareCoverUrl(link, dto.id, 640) else null,
                    )
                }
                ShareUiState.Ready(link, payload.album, payload.scope, tracks)
            } catch (e: ApiException) {
                // The server answers a uniform 404 for unknown, revoked, and out-of-scope tokens.
                if (e.status == 404) {
                    ShareUiState.Failed(link, "This share link does not exist or has been revoked.", gone = true)
                } else {
                    ShareUiState.Failed(link, "Could not load the share (${e.status}).", gone = false)
                }
            } catch (e: Exception) {
                ShareUiState.Failed(link, "Could not reach ${link.origin}.", gone = false)
            }
        }
    }

    fun retryShare() {
        _share.value?.let { openShare(it.link) }
    }

    /** Plays from the share viewer — same queue plumbing, foreign-id guards on. */
    fun playShare(tracks: List<Track>, startIndex: Int) {
        _isShareQueue.value = true
        // A share track can collide with a library id; force the next lyrics/video fetch.
        lyricsSongId = null
        player.play(tracks, startIndex)
    }

    fun closeShare() {
        shareJob?.cancel()
        _share.value = null
        if (_isShareQueue.value) {
            // Nothing should keep streaming another server's audio behind the library (or the
            // pairing screen, when this viewer was the only thing on screen).
            player.stop()
            _isShareQueue.value = false
        }
    }

    // ---- Friend invites ------------------------------------------------------------------------

    private val _invite = MutableStateFlow<InviteUiState?>(null)
    val invite: StateFlow<InviteUiState?> = _invite.asStateFlow()

    private var inviteJob: Job? = null

    /** Peeks the invite — never consumes the single-use token; that takes the explicit Accept. */
    fun openInvite(link: InviteLink) {
        inviteJob?.cancel()
        _invite.value = InviteUiState.Loading(link)
        inviteJob = viewModelScope.launch {
            _invite.value = try {
                val peek = graph.api.peekInvite(link)
                InviteUiState.Ready(link, peek.inviterName?.takeIf { it.isNotBlank() } ?: "Someone", peek.email)
            } catch (e: ApiException) {
                InviteUiState.Failed(link, "This invite does not exist or has expired.", gone = e.status == 404)
            } catch (e: Exception) {
                InviteUiState.Failed(link, "Could not reach ${link.origin}.", gone = false)
            }
        }
    }

    fun retryInvite() {
        _invite.value?.let { openInvite(it.link) }
    }

    fun dismissInvite() {
        inviteJob?.cancel()
        _invite.value = null
    }

    /**
     * Consumes the invite and pairs this phone as the new Friend account. Goes through [pair]'s
     * probe-then-save path, so the role lands as the server reports it and the old library state
     * is torn down exactly like any other re-pair.
     */
    fun acceptInvite() {
        val ready = _invite.value as? InviteUiState.Ready ?: return
        _invite.value = InviteUiState.Accepting(ready.link, ready.inviterName, ready.email)
        inviteJob = viewModelScope.launch {
            val result = runCatching { graph.api.acceptInvite(ready.link) }
            val token = result.getOrNull()?.takeIf { it.isNotBlank() }
            if (token == null) {
                val gone = (result.exceptionOrNull() as? ApiException)?.status in setOf(400, 404)
                _invite.value = InviteUiState.Failed(
                    ready.link,
                    if (gone) "This invite does not exist or has expired." else "Could not reach ${ready.link.origin}.",
                    gone = gone,
                )
                return@launch
            }
            _invite.value = null
            closeShare()
            pair(ServerSession(ready.link.origin, token))
        }
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

            _isShareQueue.value = false
            video.load(null)
            player.stop()
            graph.library.clear()

            val next = graph.sessions.accounts.value.active
            // Nothing left to add an account *alongside*: whatever the switcher opened degrades to
            // the first-run sign-in screen, which must not offer a Cancel back to an account gone.
            if (next == null) _addingAccount.value = false
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

    /**
     * Tapping an artist narrows the Albums tab in place, the way the web's `?artist=` link does.
     *
     * Any open drilldown goes with it: an album screen sits *over* the grid this narrows, so coming
     * from the player it would mask the very page the tap asked for.
     */
    fun openArtist(name: String) = _ui.update {
        it.copy(artistFilter = name, tab = LibraryTab.Albums, letter = null, openAlbumKey = null)
    }

    fun clearArtistFilter() = _ui.update { it.copy(artistFilter = null) }

    fun openAlbum(album: Album) = openAlbumKey(album.key)

    /**
     * The same drilldown addressed by key, for a caller holding one rather than a card — the player
     * knows its track's folder, not the grid it would be a tile in. [resolveAlbum] does the
     * matching, so a folder key that lost a name merge still lands on the card that survived.
     */
    fun openAlbumKey(key: String) = _ui.update { it.copy(openAlbumKey = key) }

    fun closeAlbum() = _ui.update { it.copy(openAlbumKey = null) }

    /** Loads the album grid's link-status dots for what is currently on screen. */
    fun ensureAlbumStatuses(albums: List<Album>) =
        graph.library.loadAlbumStatuses(albums, viewModelScope)

    fun toggleLike(track: Track) {
        // The server enforces this; refusing here just avoids an optimistic heart that flips back
        // a moment later with a 403. Capabilities are refreshed on foreground, so a revoke takes
        // effect without re-pairing — and an empty set (a phone that has not refreshed yet) is
        // treated as "allowed" so the button never dead-ends on a stale local blank.
        if (_capabilities.value.isNotEmpty() && !can(CAPABILITY_TRACK_LISTENING)) {
            _localMessages.tryEmit("Liking is turned off for your account.")
            return
        }
        viewModelScope.launch { graph.library.toggleLike(track) }
    }

    /** The artist portrait endpoint. 404s are common and simply never paint. */
    fun artistImageUrl(name: String): String = graph.api.artistImageUrl(name)

    fun play(tracks: List<Track>, startIndex: Int) {
        if (_isShareQueue.value) {
            _isShareQueue.value = false
            lyricsSongId = null
        }
        player.play(tracks, startIndex)
    }

    /** Null when there is nothing to show — callers fall back to a letter tile. */
    fun coverUrl(trackId: Int?, hasCover: Boolean, size: Int): String? =
        if (trackId != null && hasCover) graph.api.coverUrl(trackId, size) else null

    override fun onCleared() {
        player.release()
        video.release()
        super.onCleared()
    }
}
