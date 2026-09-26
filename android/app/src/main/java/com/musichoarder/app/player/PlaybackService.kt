package com.musichoarder.app.player

import android.app.PendingIntent
import android.content.Intent
import androidx.annotation.OptIn
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.DataSource
import androidx.media3.datasource.DataSourceBitmapLoader
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.session.CacheBitmapLoader
import androidx.media3.session.MediaSession
import androidx.media3.session.MediaSessionService
import com.musichoarder.app.MainActivity
import com.musichoarder.app.MusicHoarderApp

/** Previous restarts the current track past this position (ms), else steps back. */
private const val PREVIOUS_RESTART_AFTER_MS = 3_000L

/**
 * Keeps playback alive outside the app: a foreground service with the system media notification,
 * lock-screen controls, and Bluetooth/headset keys — the OS-level equivalent of the web player's
 * Media Session integration.
 *
 * Both the audio stream and the notification's artwork are fetched through the app's authenticated
 * OkHttp client, since every MusicHoarder endpoint requires the bearer token.
 *
 * The player is lent to playback sync ([PlaybackConnect]) for as long as the service lives, and the
 * session drives it through [PlaybackConnect.sessionPlayer] — so a play from the lock screen claims
 * the account's session just as a tap in the app does, and the Mac taking the session over pauses
 * this player even with the app swiped away.
 */
@OptIn(UnstableApi::class)
class PlaybackService : MediaSessionService() {
    private var mediaSession: MediaSession? = null
    private var player: ExoPlayer? = null

    override fun onCreate() {
        super.onCreate()
        val graph = (application as MusicHoarderApp).graph
        val dataSourceFactory: DataSource.Factory =
            MediaSources.dataSourceFactory(this, graph.httpClient)

        val player = ExoPlayer.Builder(this)
            .setMediaSourceFactory(MediaSources.mediaSourceFactory(dataSourceFactory))
            .setAudioAttributes(
                AudioAttributes.Builder()
                    .setUsage(C.USAGE_MEDIA)
                    .setContentType(C.AUDIO_CONTENT_TYPE_MUSIC)
                    .build(),
                /* handleAudioFocus = */ true,
            )
            // Pause when the headphones are yanked out, like every other music app.
            .setHandleAudioBecomingNoisy(true)
            // Previous (the transport, the notification, a headset) restarts the track once it is
            // more than 3s in, else steps back, and restarts the first item: `seekToPrevious` with
            // this threshold is exactly the web player's `previousAction`
            // (PREVIOUS_RESTART_AFTER_S in player-seek.ts). Pinned rather than left to Media3's
            // default so the two clients cannot drift apart if that default ever moves.
            .setMaxSeekToPreviousPositionMs(PREVIOUS_RESTART_AFTER_MS)
            .build()
        this.player = player
        graph.playbackConnect.attach(player)

        mediaSession = MediaSession.Builder(this, graph.playbackConnect.sessionPlayer(player))
            .setBitmapLoader(
                CacheBitmapLoader(
                    DataSourceBitmapLoader.Builder(this)
                        .setDataSourceFactory(dataSourceFactory)
                        .build()
                )
            )
            .setSessionActivity(
                PendingIntent.getActivity(
                    this,
                    0,
                    Intent(this, MainActivity::class.java),
                    PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
                )
            )
            .build()
    }

    override fun onGetSession(controllerInfo: MediaSession.ControllerInfo): MediaSession? = mediaSession

    /**
     * Swiping the app away should not strand a silent foreground service in the shade — but it must
     * not kill music that is still playing either.
     */
    override fun onTaskRemoved(rootIntent: Intent?) {
        val player = mediaSession?.player
        if (player == null || !player.playWhenReady || player.mediaItemCount == 0) {
            stopSelf()
        }
    }

    override fun onDestroy() {
        player?.let { (application as MusicHoarderApp).graph.playbackConnect.detach(it) }
        player = null
        mediaSession?.run {
            player.release()
            release()
        }
        mediaSession = null
        super.onDestroy()
    }
}
