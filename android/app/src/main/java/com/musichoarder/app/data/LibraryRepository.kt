package com.musichoarder.app.data

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

data class LibraryState(
    val isLoading: Boolean = false,
    val tracks: List<Track> = emptyList(),
    val albums: List<Album> = emptyList(),
    val error: String? = null,
    val isPairingRevoked: Boolean = false,
) {
    val isEmpty: Boolean get() = tracks.isEmpty()
}

/**
 * Holds the library in memory. `GET /songs` is a whole-library dump (the web app does the same), so
 * it is fetched once per app start and refreshed on pull-to-refresh rather than per screen.
 */
class LibraryRepository(private val api: MusicHoarderApi) {
    private val _state = MutableStateFlow(LibraryState())
    val state: StateFlow<LibraryState> = _state.asStateFlow()

    private val loadMutex = Mutex()

    suspend fun refresh(force: Boolean = false) {
        loadMutex.withLock {
            if (_state.value.tracks.isNotEmpty() && !force) return
            _state.value = _state.value.copy(isLoading = true, error = null, isPairingRevoked = false)
            try {
                val tracks = api.fetchSongs()
                    // Only the clean output, exactly as every "Listen" surface on the web does.
                    // A scan indexes everything it finds; until the builder has copied and tagged a
                    // song into the destination it is pipeline state, not library.
                    .filter { it.isBuilt }
                    .map { it.toTrack() }
                    .sortedWith(
                        compareBy(
                            { it.artist.lowercase() },
                            { it.album.lowercase() },
                            { it.trackNumber ?: Int.MAX_VALUE },
                            { it.title.lowercase() },
                        )
                    )
                _state.value = LibraryState(tracks = tracks, albums = tracks.toAlbums())
            } catch (e: UnauthorizedException) {
                _state.value = _state.value.copy(isLoading = false, error = e.message, isPairingRevoked = true)
            } catch (e: Exception) {
                _state.value = _state.value.copy(
                    isLoading = false,
                    error = e.message ?: "Could not reach the server.",
                )
            }
        }
    }

    fun trackById(id: Int): Track? = _state.value.tracks.firstOrNull { it.id == id }

    fun clear() {
        _state.value = LibraryState()
    }
}
