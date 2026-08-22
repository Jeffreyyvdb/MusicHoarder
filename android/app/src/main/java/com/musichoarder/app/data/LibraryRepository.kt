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

    /**
     * The hearted songs, kept beside the track list rather than on [Track].
     *
     * A like has to repaint one button, and copying the whole (whole-library) track list to flip one
     * boolean would repaint every list that reads it. The set is seeded from the dump's `likedAtUtc`
     * and is the only thing [setLiked] touches.
     */
    private val _likedIds = MutableStateFlow<Set<Int>>(emptySet())
    val likedIds: StateFlow<Set<Int>> = _likedIds.asStateFlow()

    private val loadMutex = Mutex()

    suspend fun refresh(force: Boolean = false) {
        loadMutex.withLock {
            if (_state.value.tracks.isNotEmpty() && !force) return
            _state.value = _state.value.copy(isLoading = true, error = null, isPairingRevoked = false)
            try {
                val songs = api.fetchSongs()
                val tracks = songs
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
                _likedIds.value = songs.filter { it.isLiked }.map { it.id }.toSet()
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

    /** Flips one song's heart. Callers do this optimistically and call again to roll back. */
    fun setLiked(songId: Int, liked: Boolean) {
        _likedIds.value = if (liked) _likedIds.value + songId else _likedIds.value - songId
    }

    fun clear() {
        _state.value = LibraryState()
        _likedIds.value = emptySet()
    }
}
