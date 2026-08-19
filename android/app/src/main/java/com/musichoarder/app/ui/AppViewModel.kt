package com.musichoarder.app.ui

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.musichoarder.app.MusicHoarderApp
import com.musichoarder.app.data.PairingUri
import com.musichoarder.app.data.ServerSession
import com.musichoarder.app.data.Track
import com.musichoarder.app.player.PlayerController
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

class AppViewModel(application: Application) : AndroidViewModel(application) {
    private val graph = (application as MusicHoarderApp).graph

    val session: StateFlow<ServerSession?> = graph.sessions.session
    val library = graph.library.state

    private val _pairError = MutableStateFlow<String?>(null)
    val pairError: StateFlow<String?> = _pairError.asStateFlow()

    val player = PlayerController(
        context = application,
        api = graph.api,
        scope = viewModelScope,
        onTrackStarted = { songId -> viewModelScope.launch { graph.api.reportPlayed(songId) } },
    )

    init {
        if (session.value != null) start()

        // A revoked or expired device session should land the user back on the pairing screen with
        // an explanation, not on an empty library that silently never loads.
        viewModelScope.launch {
            graph.library.state.collect { state ->
                if (state.isPairingRevoked) {
                    _pairError.value = state.error
                    unpair()
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

    /** Accepts a scanned QR payload, or a URL + token typed by hand. */
    fun pairFromCode(raw: String) {
        val parsed = PairingUri.parse(raw)
        if (parsed == null) {
            _pairError.value = "That code is not a MusicHoarder pairing code."
            return
        }
        pair(parsed)
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
            graph.sessions.save(newSession)
            // Confirm the token works before leaving the pairing screen — a stale QR code should
            // fail here, not as an empty library two screens later.
            val ok = runCatching { graph.api.fetchMe() }.isSuccess
            if (!ok) {
                graph.sessions.clear()
                _pairError.value = "The server did not accept that pairing code. Try a fresh one."
                return@launch
            }
            start()
        }
    }

    fun setPairError(message: String?) {
        _pairError.value = message
    }

    fun unpair() {
        viewModelScope.launch {
            player.stop()
            player.release()
            graph.library.clear()
            graph.sessions.clear()
        }
    }

    fun play(tracks: List<Track>, startIndex: Int) = player.play(tracks, startIndex)

    /** Null when there is nothing to show — callers fall back to a letter tile. */
    fun coverUrl(trackId: Int?, hasCover: Boolean, size: Int): String? =
        if (trackId != null && hasCover) graph.api.coverUrl(trackId, size) else null

    override fun onCleared() {
        player.release()
        super.onCleared()
    }
}
