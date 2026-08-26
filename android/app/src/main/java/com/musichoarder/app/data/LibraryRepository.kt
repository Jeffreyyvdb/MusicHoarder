package com.musichoarder.app.data

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.TimeZone

/** Playback facts that move without a refetch. */
data class PlayStat(val playCount: Int, val lastPlayedAtMs: Long)

data class LibraryState(
    val isLoading: Boolean = false,
    /**
     * Only the clean output - what the builder copied and tagged into the destination. The album and
     * artist grids and the Overview are built from this, exactly as on the web.
     */
    val builtTracks: List<Track> = emptyList(),
    /**
     * What the Tracks list covers: everything built, plus your own source files still waiting on
     * review. Wider on purpose - the "Local files" chip has to be able to answer "what is on my
     * share", and a scanned file sitting at NeedsReview is already yours and already playable
     * (the stream endpoint falls back to the source path). Widening the base once, rather than
     * letting a chip add rows, is what keeps every chip a pure narrowing.
     *
     * Album completion's tracks ARE in here, and `foldLibrary` drops them from the Tracks list on
     * the way past. Deliberate: this list doubles as the row resolver for the player ([trackById])
     * and as the source of the liked-id set, both of which must still answer for a filled track
     * played from the album screen.
     */
    val trackListBase: List<Track> = emptyList(),
    /** Folder-keyed then merged by name. Never scoped by a filter: this is the drilldown resolver. */
    val albums: List<Album> = emptyList(),
    val artistsPrimary: List<ArtistGroup> = emptyList(),
    val artistsAll: List<ArtistGroup> = emptyList(),
    val error: String? = null,
    val isPairingRevoked: Boolean = false,
    /** Who is sharing music with you, from the last fetch. Empty when it is all your own. */
    val grantors: List<Grantor> = emptyList(),
) {
    val isEmpty: Boolean get() = trackListBase.isEmpty()

    /** The grantor of one track, or null when this account owns it. */
    fun grantorOf(track: Track): Grantor? {
        val id = track.sharedByUserId ?: return null
        return grantors.firstOrNull { it.userId == id }
    }

    /**
     * "shared by X" for the library header, or null when nothing is shared. Names whoever actually
     * shared the rows on screen rather than assuming a single library owner.
     */
    fun sharedByLabel(): String? {
        val names = grantors.map { it.displayName?.trim()?.takeIf(String::isNotEmpty) ?: "someone" }
        return when (names.size) {
            0 -> null
            1 -> "shared by ${names[0]}"
            2 -> "shared by ${names[0]} and ${names[1]}"
            else -> "shared by ${names[0]} and ${names.size - 1} others"
        }
    }
}

/**
 * Holds the library in memory. `GET /songs` is a whole-library dump (the web app does the same), so
 * it is fetched once per app start and refreshed on demand rather than per screen.
 *
 * Likes and play counts are kept as **overlays** rather than rewritten into the [Track] rows. Albums
 * and artist groups hold references to those rows, so flipping a heart by rebuilding one track would
 * invalidate the whole album grid and re-key every tile for a single tap.
 */
class LibraryRepository(private val api: MusicHoarderApi) {
    private val _state = MutableStateFlow(LibraryState())
    val state: StateFlow<LibraryState> = _state.asStateFlow()

    /** Song id to `likedAtUtc`, overriding the fetched value. */
    private val _likes = MutableStateFlow<Map<Int, String?>>(emptyMap())
    val likes: StateFlow<Map<Int, String?>> = _likes.asStateFlow()

    private val _plays = MutableStateFlow<Map<Int, PlayStat>>(emptyMap())
    val plays: StateFlow<Map<Int, PlayStat>> = _plays.asStateFlow()

    private val _albumStatuses = MutableStateFlow<Map<String, AlbumStatus>>(emptyMap())
    val albumStatuses: StateFlow<Map<String, AlbumStatus>> = _albumStatuses.asStateFlow()

    /** Failures not worth a whole error screen. Surfaced as a snackbar. */
    private val _messages = MutableSharedFlow<String>(
        extraBufferCapacity = 4,
        onBufferOverflow = BufferOverflow.DROP_OLDEST,
    )
    val messages: SharedFlow<String> = _messages.asSharedFlow()

    private val loadMutex = Mutex()

    /** The identity set the last status lookup covered, so a silent refetch does not re-post it. */
    private var statusSignature: String? = null

    suspend fun refresh(force: Boolean = false) {
        loadMutex.withLock {
            if (_state.value.trackListBase.isNotEmpty() && !force) return
            _state.value = _state.value.copy(isLoading = true, error = null, isPairingRevoked = false)
            try {
                // Both in one go: they are two views of the same library, so fetching them apart
                // would let the album cards describe a song list that has already moved on.
                val response = api.fetchSongs()
                val albums = api.fetchAlbums()
                // Joining four thousand tracks to their albums and grouping the artists is far too
                // much work for a frame, and the mapping this replaces ran on the main dispatcher.
                _state.value = withContext(Dispatchers.Default) {
                    fold(response.songs, albums).copy(grantors = response.grantors)
                }
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

    private fun fold(songs: List<ApiSong>, albums: List<AlbumSummaryDto>): LibraryState {
        // Build state comes from ApiSong.isBuilt, which trusts the server's flag for rows shared
        // with you and derives it locally for your own. That replaced a session-wide "shared
        // library" mode this repository used to read — one rule, both row kinds, and the phone no
        // longer needs to know what kind of account it holds.
        val mapped = songs.map { song -> song.toTrack() to song.isBuilt }
        val base = mapped
            .filter { (track, isBuilt) -> isBuilt || (track.isLocalFile && track.needsReview) }
            .map { it.first }
            .sortedWith(BASE_ORDER)
        val built = mapped.filter { it.second }.map { it.first }.sortedWith(BASE_ORDER)
        // Joined against the whole base, not just the built rows: the cards name only built tracks
        // anyway, and looking them up here keeps the join a lookup rather than a second filter.
        val byId = base.associateBy { it.id }
        return LibraryState(
            builtTracks = built,
            trackListBase = base,
            albums = hydrateAlbums(albums, byId),
            artistsPrimary = buildArtistGroups(built, primaryOnly = true),
            artistsAll = buildArtistGroups(built, primaryOnly = false),
        )
    }

    /**
     * Loads the album grid's link-status dots, keyed on the identity set it is about to send so the
     * silent refetches never re-post the whole library.
     */
    fun loadAlbumStatuses(albums: List<Album>, scope: CoroutineScope) {
        // Called unconditionally. The endpoint is admin-only, so a member's request comes back
        // 403 and the dots simply do not paint — the call site is already best-effort. Guarding
        // it here would put role knowledge back into the repository to save one request.
        val identities = albums.map { AlbumIdentity(it.artist, it.name) }
        // Separators no artist or album name can contain, so two different sets cannot
        // collide on one signature.
        val signature = identities.joinToString("\u0001") { "${it.artist}\u0000${it.album}" }
        if (signature == statusSignature) return
        statusSignature = signature
        if (identities.isEmpty()) {
            _albumStatuses.value = emptyMap()
            return
        }
        scope.launch {
            // Badges are best-effort; leave them off on error rather than nagging about it.
            runCatching { api.fetchAlbumStatuses(identities) }
                .onSuccess { _albumStatuses.value = it }
        }
    }

    /** The heart's current state for a song, overlay first. */
    fun likedStamp(track: Track): String? =
        if (_likes.value.containsKey(track.id)) _likes.value[track.id] else track.likedAtUtc

    /**
     * Flips the heart optimistically, then lets the server's own stamp win. A failure reverts *and*
     * says so: a silent revert just looks like the tap missed.
     */
    suspend fun toggleLike(track: Track) {
        val previous = likedStamp(track)
        val wantLiked = previous == null
        _likes.update { it + (track.id to if (wantLiked) nowIsoUtc() else null) }
        try {
            _likes.update { it + (track.id to api.setLiked(track.id, wantLiked)) }
        } catch (e: Exception) {
            _likes.update { it + (track.id to previous) }
            _messages.tryEmit("Could not update liked songs")
        }
    }

    /**
     * Mirrors what `POST /songs/{id}/played` will record, so the Overview's "Last played" and
     * "Discover - never played" move as you listen instead of waiting for the next full fetch.
     */
    fun notePlayed(songId: Int, atMillis: Long) {
        val known = _plays.value[songId]?.playCount ?: trackById(songId)?.playCount ?: 0
        _plays.update { it + (songId to PlayStat(known + 1, atMillis)) }
    }

    fun playCountOf(track: Track): Int = _plays.value[track.id]?.playCount ?: track.playCount

    fun lastPlayedAtMsOf(track: Track): Long =
        _plays.value[track.id]?.lastPlayedAtMs ?: track.lastPlayedAtMs

    fun trackById(id: Int): Track? = _state.value.trackListBase.firstOrNull { it.id == id }

    fun clear() {
        _state.value = LibraryState()
        _likes.value = emptyMap()
        _plays.value = emptyMap()
        _albumStatuses.value = emptyMap()
        statusSignature = null
    }

    private companion object {
        /**
         * The list's resting order, and the tie-break every explicit sort falls back to - the same
         * ordering `/songs` itself returns.
         */
        val BASE_ORDER: Comparator<Track> = compareBy(
            { it.albumArtist.lowercase() },
            { it.album.lowercase() },
            { it.trackNumber ?: Int.MAX_VALUE },
            { it.title.lowercase() },
        )
    }
}

/** The optimistic like's placeholder stamp, replaced by the server's own the moment it answers. */
private fun nowIsoUtc(): String {
    val format = SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss.SSS'Z'", Locale.US)
    format.timeZone = TimeZone.getTimeZone("UTC")
    return format.format(Date())
}
