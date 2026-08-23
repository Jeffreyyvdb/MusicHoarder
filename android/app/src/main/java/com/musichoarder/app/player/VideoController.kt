package com.musichoarder.app.player

import android.content.Context
import android.os.SystemClock
import android.view.TextureView
import androidx.annotation.OptIn
import androidx.media3.common.MediaItem
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.VideoSize
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.SeekParameters
import com.musichoarder.app.data.MusicHoarderApi
import com.musichoarder.app.data.VideoInfo
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import okhttp3.OkHttpClient
import kotlin.math.abs

data class VideoState(
    val songId: Int? = null,
    val info: VideoInfo? = null,
    /** True once the clip is attached and should be painted over the artwork. */
    val isVisible: Boolean = false,
    /**
     * The decoded frame's width / height, pixel aspect ratio included. Null until the first frame
     * arrives — the API's `VideoInfo` carries no dimensions, so the decoder is the only source.
     */
    val aspectRatio: Float? = null,
    /**
     * The clip is done for this playthrough — it ran past its end, or both stream retries failed.
     *
     * Distinct from [isVisible], which also goes false for benign reasons (the sheet closed, the
     * backdrop was switched off). Only a retired clip should take the Video tab away with it.
     */
    val isRetired: Boolean = false,
) {
    val hasVideo: Boolean get() = info?.isPlayable == true

    /** Whether there is still a clip worth offering a Video tab for. */
    val isWatchable: Boolean get() = hasVideo && !isRetired
}

/**
 * The muted music video that plays behind the now-playing screen.
 *
 * The audio is the master clock, always: this player never drives playback, it only chases the
 * audio position (`videoTime = audioTime + syncOffsetMs / 1000`) and hard-seeks whenever it drifts
 * further than [DRIFT_TOLERANCE_MS]. That is the same contract the web backdrop follows, so a clip
 * with an intro stays lined up with the song rather than the file.
 */
@OptIn(UnstableApi::class)
class VideoController(
    context: Context,
    private val api: MusicHoarderApi,
    httpClient: OkHttpClient,
    private val scope: CoroutineScope,
) {
    private val appContext = context.applicationContext

    private val _state = MutableStateFlow(VideoState())
    val state: StateFlow<VideoState> = _state.asStateFlow()

    private val player: ExoPlayer = ExoPlayer.Builder(appContext)
        .setMediaSourceFactory(
            MediaSources.mediaSourceFactory(MediaSources.dataSourceFactory(appContext, httpClient))
        )
        .build()
        .apply {
            // The song is the audio; the clip is wallpaper.
            volume = 0f
            repeatMode = Player.REPEAT_MODE_OFF
            // Corrective seeks land on the nearest keyframe rather than decoding forward from one.
            // Nothing here needs frame accuracy — the clip is chasing a clock it is already allowed
            // to be a quarter of a second off — and an exact seek costs a full re-decode every time,
            // which over a phone connection is the difference between a nudge and a stall.
            setSeekParameters(SeekParameters.CLOSEST_SYNC)
            addListener(object : Player.Listener {
                override fun onPlayerError(error: PlaybackException) = onLoadError()

                // Without this nothing in the app knows the clip's shape, and the surface — which
                // ExoPlayer never resizes on its own — stretches whatever it is given to fill.
                override fun onVideoSizeChanged(size: VideoSize) {
                    val ratio = if (size.width == 0 || size.height == 0) null
                    else size.width * size.pixelWidthHeightRatio / size.height
                    _state.value = _state.value.copy(aspectRatio = ratio)
                }
            })
        }

    private var loadJob: Job? = null
    private var retries = 0
    /** Set once the clip runs past its end while the song keeps going — fall back to artwork. */
    private var ended = false
    /** When the last corrective seek went out, on the monotonic clock. See [shouldResyncVideo]. */
    private var lastResyncAtMs = 0L

    /**
     * A [TextureView], not a `SurfaceView`.
     *
     * A SurfaceView lives in its own layer behind the window and is only visible through a hole it
     * punches in whatever the window painted. That cannot survive this screen: the clip sits under
     * an ambient wash, a scrim and a gradient, and the blur promotes part of that stack into its own
     * graphics layer. The decoder ran and nothing appeared. A TextureView composites in the ordinary
     * view draw order, so the layers above it behave like layers — and it is captured by
     * screenshots, which a surface is not.
     */
    fun attachSurface(textureView: TextureView) = player.setVideoTextureView(textureView)

    fun clearSurface() = player.clearVideoSurface()

    /**
     * Matches the audio's rate. The clip is slaved to the audio clock and hard-seeks past its drift
     * tolerance; left at 1x while the song runs at 1.5x it would fall behind fast enough to be
     * re-seeking on almost every tick.
     *
     * Changing the rate makes the renderers re-prime, so the clip is given the usual cooldown to
     * settle at the new speed before it is judged for drift again.
     */
    fun setSpeed(rate: Float) {
        val clamped = rate.coerceIn(0.25f, 2f)
        if (player.playbackParameters.speed == clamped) return
        player.setPlaybackSpeed(clamped)
        lastResyncAtMs = SystemClock.elapsedRealtime()
    }

    /** Looks up the song's video and prepares it. Safe to call on every track change. */
    fun load(songId: Int?) {
        if (_state.value.songId == songId) return
        loadJob?.cancel()
        player.stop()
        player.clearMediaItems()
        retries = 0
        ended = false
        // A fresh clip has to be placed on the song's clock straight away, not a cooldown later.
        lastResyncAtMs = 0L
        _state.value = VideoState(songId = songId)
        if (songId == null) return

        loadJob = scope.launch {
            // Availability may only settle on a real answer. `fetchVideoInfo` returns null for a
            // genuine 404 (no video attached) and throws for anything else, so a proxy blip or an
            // API restart is retried instead of being remembered as "this song has no video" for
            // the rest of the track — the regression the web client already fixed.
            var info: VideoInfo? = null
            for (attempt in 0..INFO_RETRY_DELAYS_MS.size) {
                val probe = runCatching { api.fetchVideoInfo(songId) }
                if (_state.value.songId != songId) return@launch
                if (probe.isSuccess) {
                    info = probe.getOrNull()
                    break
                }
                if (attempt == INFO_RETRY_DELAYS_MS.size) return@launch
                delay(INFO_RETRY_DELAYS_MS[attempt])
                if (_state.value.songId != songId) return@launch
            }
            _state.value = _state.value.copy(info = info)
            if (info?.isPlayable != true) return@launch
            player.setMediaItem(MediaItem.fromUri(api.videoStreamUrl(songId)))
            player.prepare()
            // Nothing plays until `sync` says the audio is running — the clip must not race ahead
            // of a paused song.
            player.playWhenReady = false
        }
    }

    /**
     * Chases [audioPositionMs]. Called from the UI's position ticker, so it runs only while the
     * player screen is on-screen — a backdrop nobody is looking at should not be decoding video.
     */
    fun sync(audioPositionMs: Long, isPlaying: Boolean) {
        val info = _state.value.info ?: return
        if (!info.isPlayable) return

        val mapped = audioPositionMs + info.syncOffsetMs

        // Re-check on every tick rather than only when the full player's slider seeks: a seek from
        // the lock screen, the notification or a headset button never reaches `onSeek`, and the clip
        // would otherwise stay retired for the rest of the track. This mirrors the web's effect.
        if (ended && isBackBeforeClipEnd(mapped)) {
            ended = false
            _state.value = _state.value.copy(isRetired = false)
        }
        if (ended) return

        if (mapped < 0) {
            // The song sits before the clip's start: hold the first frame until the audio catches up.
            if (player.isPlaying) player.pause()
            if (player.currentPosition != 0L) player.seekTo(0)
            return
        }

        val duration = player.duration
        if (duration > 0 && mapped >= duration - END_GUARD_MS) {
            // The song outlives the clip — fall back to the artwork.
            ended = true
            player.pause()
            _state.value = _state.value.copy(isVisible = false, isRetired = true)
            return
        }

        val now = SystemClock.elapsedRealtime()
        if (
            shouldResyncVideo(
                driftMs = player.currentPosition - mapped,
                rate = player.playbackParameters.speed,
                isBuffering = player.playbackState != Player.STATE_READY,
                sinceLastResyncMs = now - lastResyncAtMs,
            )
        ) {
            lastResyncAtMs = now
            player.seekTo(mapped)
        }
        if (isPlaying && !player.isPlaying) {
            player.play()
        } else if (!isPlaying && player.isPlaying) {
            player.pause()
        }
        if (!_state.value.isVisible) _state.value = _state.value.copy(isVisible = true)
    }

    /**
     * Stops chasing and parks the clip. `sync` is the only thing that ever starts the video, so
     * without this the player kept streaming and decoding after the sheet was collapsed — nothing
     * was left running to pause it.
     */
    fun pause() {
        if (player.isPlaying) player.pause()
        if (_state.value.isVisible) _state.value = _state.value.copy(isVisible = false)
    }

    /** A seek back before the clip's end brings the video back, without waiting for the next tick. */
    fun onSeek(audioPositionMs: Long) {
        val info = _state.value.info ?: return
        // The user moved the song, so the clip is not drifting, it is in the wrong place — it should
        // follow on the very next tick rather than sitting out the rest of a cooldown.
        lastResyncAtMs = 0L
        if (ended && isBackBeforeClipEnd(audioPositionMs + info.syncOffsetMs)) {
            ended = false
            _state.value = _state.value.copy(isRetired = false)
        }
    }

    /**
     * Whether the mapped position is far enough before the clip's end to show it again. Prefers the
     * player's own duration and falls back to the API's, so a null `durationSeconds` no longer means
     * the video can never come back.
     */
    private fun isBackBeforeClipEnd(mappedMs: Long): Boolean {
        val duration = player.duration.takeIf { it > 0 }
            ?: _state.value.info?.durationSeconds?.times(1000L)
            ?: return false
        return mappedMs < duration - 1000
    }

    /**
     * A dropped stream request (proxy blip, API restarting) would otherwise strand the backdrop for
     * the rest of the song. Give it a couple of reloads before giving up.
     */
    private fun onLoadError() {
        if (retries >= RETRY_DELAYS_MS.size) {
            _state.value = _state.value.copy(isVisible = false, isRetired = true)
            return
        }
        val attempt = retries
        retries = attempt + 1
        val songId = _state.value.songId
        scope.launch {
            delay(RETRY_DELAYS_MS[attempt])
            if (_state.value.songId != songId) return@launch
            player.prepare()
        }
    }

    fun release() {
        loadJob?.cancel()
        player.release()
    }

    private companion object {
        const val END_GUARD_MS = 50L
        val RETRY_DELAYS_MS = longArrayOf(1000, 3000)
        val INFO_RETRY_DELAYS_MS = longArrayOf(1000, 3000)
    }
}

/** The web backdrop's tolerance at 1x: below this, a seek would be more jarring than the drift. */
private const val DRIFT_TOLERANCE_MS = 300L

/** The least wall clock between two corrective seeks. */
private const val RESYNC_COOLDOWN_MS = 1_000L

/**
 * Drift that is not drift but dislocation: the clip is on a different part of the song altogether,
 * which is what a scrub of the *audio* leaves behind. Worth interrupting a buffering clip for.
 */
private const val GROSS_DRIFT_MS = 2_000L

/**
 * Whether the clip should be hard-seeked back onto the audio clock on this tick.
 *
 * A corrective seek is not free: it throws away the buffer and re-primes the decoder over the same
 * connection the song is streaming on. That is affordable once in a while and ruinous in a loop —
 * and a loop is exactly what the old unconditional `abs(drift) > 300ms` check produced above 1x,
 * which is why the picture froze at 1.5x and 2x. A seek pins the reported position at its target
 * while the request goes out, so on the next tick the song has moved another 200·rate ms and the
 * drift is *larger* than the one that triggered the seek: correct, re-buffer, fall further behind,
 * correct again, forever, with no frame ever reaching the surface.
 *
 * Three guards break that cycle:
 *  - [sinceLastResyncMs] rate-limits corrections, so the decoder always gets a stretch of wall clock
 *    to catch up under its own steam — usually all it needed;
 *  - a clip that is still buffering ([isBuffering]) is not drifting, it is waiting, and re-seeking it
 *    only restarts the wait. Only [GROSS_DRIFT_MS] — the clip being on the wrong part of the song
 *    entirely — is worth interrupting for;
 *  - the tolerance scales with [rate], because one 200 ms UI tick moves the song 200·rate ms and a
 *    fixed 300 ms window is already blown by a single tick's worth of stall at 2x.
 */
internal fun shouldResyncVideo(
    driftMs: Long,
    rate: Float,
    isBuffering: Boolean,
    sinceLastResyncMs: Long,
): Boolean {
    if (sinceLastResyncMs < RESYNC_COOLDOWN_MS) return false
    val drift = abs(driftMs)
    if (isBuffering) return drift > GROSS_DRIFT_MS
    return drift > driftToleranceMs(rate)
}

/**
 * How far the clip may wander at [rate] before it is pulled back. Speeds below 1x are held to the
 * 1x tolerance — a slow song leaves plenty of room to correct in, and loosening it there would only
 * make the lip-sync worse where it is easiest to see.
 */
internal fun driftToleranceMs(rate: Float): Long =
    (DRIFT_TOLERANCE_MS * rate.coerceAtLeast(1f)).toLong()
