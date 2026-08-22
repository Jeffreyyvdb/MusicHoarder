package com.musichoarder.app.player

import android.content.ComponentName
import android.content.Context
import android.net.Uri
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.session.MediaController
import androidx.media3.session.SessionToken
import com.musichoarder.app.data.MusicHoarderApi
import com.musichoarder.app.data.Track
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import com.google.common.util.concurrent.ListenableFuture
import com.google.common.util.concurrent.MoreExecutors
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

/** Everything the player UI renders, flattened out of the [Player] callbacks. */
data class PlayerUiState(
    val trackId: Int? = null,
    val title: String = "",
    val artist: String = "",
    val album: String = "",
    val hasCover: Boolean = false,
    val isPlaying: Boolean = false,
    val isBuffering: Boolean = false,
    val positionMs: Long = 0,
    val durationMs: Long = 0,
    val hasNext: Boolean = false,
    val hasPrevious: Boolean = false,
    val shuffleEnabled: Boolean = false,
    val repeatMode: Int = Player.REPEAT_MODE_OFF,
    val playbackRate: Float = 1f,
    val error: String? = null,
) {
    val isActive: Boolean get() = trackId != null
}

/**
 * The app's handle on the [PlaybackService]: connects a [MediaController], mirrors its state into a
 * flow Compose can collect, and turns [Track]s into the queue.
 */
class PlayerController(
    private val context: Context,
    private val api: MusicHoarderApi,
    private val scope: CoroutineScope,
    private val onTrackStarted: (Int) -> Unit,
) {
    private val _state = MutableStateFlow(PlayerUiState())
    val state: StateFlow<PlayerUiState> = _state.asStateFlow()

    private var controller: MediaController? = null
    /** Guards the async bind: `controller != null` alone let two overlapping calls build two. */
    private var connectJob: Job? = null
    private var tickerJob: Job? = null
    /** Guards against reporting the same play twice when the player re-reads a transition. */
    private var lastReportedTrackId: Int? = null
    /**
     * A tap that lands before the service connection completes. Binding to the [PlaybackService]
     * takes a moment on a cold start, and dropping the first tap of the session — the one that
     * starts the music — is the worst possible thing to drop.
     */
    private var pendingPlay: Pair<List<Track>, Int>? = null

    private val listener = object : Player.Listener {
        override fun onEvents(player: Player, events: Player.Events) = pushState(player)

        override fun onPlayerErrorChanged(error: PlaybackException?) {
            _state.value = _state.value.copy(error = error?.let { "Playback failed: ${it.errorCodeName}" })
        }
    }

    fun connect() {
        if (controller != null || connectJob?.isActive == true) return
        connectJob = scope.launch {
            val token = SessionToken(context, ComponentName(context, PlaybackService::class.java))
            val connected = runCatching { MediaController.Builder(context, token).buildAsync().await() }
                .getOrNull() ?: return@launch

            controller = connected
            connected.addListener(listener)
            pushState(connected)
            startPositionTicker()
            pendingPlay?.let { (tracks, index) ->
                pendingPlay = null
                play(tracks, index)
            }
        }
    }

    fun release() {
        connectJob?.cancel()
        connectJob = null
        // Without this the loop kept spinning for the ViewModel's lifetime, and every re-pair
        // started another one on top.
        tickerJob?.cancel()
        tickerJob = null
        controller?.removeListener(listener)
        controller?.release()
        controller = null
        _state.value = PlayerUiState()
    }

    /** Plays [tracks] from [startIndex] — the tapped row becomes the queue's current item. */
    fun play(tracks: List<Track>, startIndex: Int) {
        if (tracks.isEmpty()) return
        val player = controller ?: run {
            pendingPlay = tracks to startIndex
            connect()
            return
        }
        player.setMediaItems(tracks.map(::toMediaItem), startIndex.coerceIn(tracks.indices), 0L)
        player.prepare()
        player.play()
    }

    fun togglePlayPause() {
        val player = controller ?: return
        if (player.isPlaying) player.pause() else player.play()
    }

    fun next() {
        controller?.seekToNextMediaItem()
    }

    /**
     * Restarts the track when it is more than a few seconds in, otherwise steps back — the
     * convention every music player shares, and what `seekToPrevious` already implements.
     */
    fun previous() {
        controller?.seekToPrevious()
    }

    fun seekTo(positionMs: Long) {
        controller?.seekTo(positionMs)
        _state.value = _state.value.copy(positionMs = positionMs)
    }

    fun toggleShuffle() {
        val player = controller ?: return
        player.shuffleModeEnabled = !player.shuffleModeEnabled
    }

    /**
     * Pitch-preserved playback speed. Media3 runs a non-1x rate through Sonic, which keeps the pitch
     * — the same contract as the web player's `preservesPitch = true`.
     */
    fun setPlaybackSpeed(rate: Float) {
        controller?.setPlaybackSpeed(rate.coerceIn(0.25f, 2f))
    }

    /** Off → repeat all → repeat one → off. */
    fun cycleRepeatMode() {
        val player = controller ?: return
        player.repeatMode = when (player.repeatMode) {
            Player.REPEAT_MODE_OFF -> Player.REPEAT_MODE_ALL
            Player.REPEAT_MODE_ALL -> Player.REPEAT_MODE_ONE
            else -> Player.REPEAT_MODE_OFF
        }
    }

    fun stop() {
        pendingPlay = null
        controller?.stop()
        controller?.clearMediaItems()
        lastReportedTrackId = null
        _state.value = PlayerUiState()
    }

    private fun toMediaItem(track: Track): MediaItem = MediaItem.Builder()
        .setMediaId(track.id.toString())
        .setUri(api.streamUrl(track.id))
        .setMediaMetadata(
            MediaMetadata.Builder()
                .setTitle(track.title)
                .setArtist(track.artist)
                .setAlbumTitle(track.album)
                .setAlbumArtist(track.albumArtist)
                // The library already knows how long the track is. ExoPlayer only learns it once
                // it has parsed enough of the stream, which over the internet can take most of a
                // minute — and until then the bar is inert and the label reads "--:--". This is
                // the web transport's `fallbackDuration` prop, carried on the item.
                .setDurationMs(track.durationMs)
                .setArtworkUri(
                    // 640 is the largest server-side thumbnail bucket — enough for the lock screen.
                    if (track.hasCover) Uri.parse(api.coverUrl(track.id, 640)) else null
                )
                .setIsBrowsable(false)
                .setIsPlayable(true)
                .build()
        )
        .build()

    private fun pushState(player: Player) {
        val metadata = player.mediaMetadata
        val trackId = player.currentMediaItem?.mediaId?.toIntOrNull()
        _state.value = _state.value.copy(
            trackId = trackId,
            title = metadata.title?.toString().orEmpty(),
            artist = metadata.artist?.toString().orEmpty(),
            album = metadata.albumTitle?.toString().orEmpty(),
            hasCover = metadata.artworkUri != null,
            isPlaying = player.isPlaying,
            isBuffering = player.playbackState == Player.STATE_BUFFERING,
            positionMs = player.currentPosition.coerceAtLeast(0),
            durationMs = player.durationOr(metadata.durationMs),
            hasNext = player.hasNextMediaItem(),
            hasPrevious = player.hasPreviousMediaItem(),
            shuffleEnabled = player.shuffleModeEnabled,
            repeatMode = player.repeatMode,
            playbackRate = player.playbackParameters.speed,
        )

        // Count a play once the track actually starts, not when it is queued — the same moment the
        // web player reports one.
        if (trackId != null && trackId != lastReportedTrackId && player.isPlaying) {
            lastReportedTrackId = trackId
            onTrackStarted(trackId)
        }
    }

    /**
     * The player only emits events on state changes, so the progress bar needs its own tick. 200 ms
     * roughly matches the web player's 10 Hz writes — at the 500 ms this used to run at, the thumb
     * moved twice a second and a scrub looked like it was snapping rather than tracking.
     *
     * It samples while paused too: a seek made with playback stopped has to show up somewhere, and
     * duration often only becomes known after the first buffer. Re-writing an unchanged value costs
     * nothing, since StateFlow compares by equality before emitting.
     */
    private fun startPositionTicker() {
        tickerJob?.cancel()
        tickerJob = scope.launch {
            while (true) {
                delay(200)
                val player = controller ?: continue
                _state.value = _state.value.copy(
                    positionMs = player.currentPosition.coerceAtLeast(0),
                    durationMs = player.durationOr(player.mediaMetadata.durationMs),
                )
            }
        }
    }
}

/** The real duration once the stream has been parsed, else the length the library reported. */
private fun Player.durationOr(fallbackMs: Long?): Long =
    duration.takeIf { it > 0 } ?: fallbackMs?.takeIf { it > 0 } ?: 0

/**
 * Awaits a Guava future without pulling in `kotlinx-coroutines-guava` for the single call site that
 * needs it (connecting the [MediaController]).
 */
private suspend fun <T> ListenableFuture<T>.await(): T = suspendCancellableCoroutine { continuation ->
    addListener(
        {
            try {
                continuation.resume(get())
            } catch (e: Exception) {
                continuation.resumeWithException(e)
            }
        },
        MoreExecutors.directExecutor(),
    )
    continuation.invokeOnCancellation { cancel(false) }
}
