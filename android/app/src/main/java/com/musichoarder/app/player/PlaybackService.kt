package com.musichoarder.app.player

import android.app.PendingIntent
import android.content.Intent
import androidx.annotation.OptIn
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.DataSource
import androidx.media3.datasource.DataSourceBitmapLoader
import androidx.media3.datasource.DefaultDataSource
import androidx.media3.datasource.okhttp.OkHttpDataSource
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.session.CacheBitmapLoader
import androidx.media3.session.MediaSession
import androidx.media3.session.MediaSessionService
import com.musichoarder.app.MainActivity
import com.musichoarder.app.MusicHoarderApp

/**
 * Keeps playback alive outside the app: a foreground service with the system media notification,
 * lock-screen controls, and Bluetooth/headset keys — the OS-level equivalent of the web player's
 * Media Session integration.
 *
 * Both the audio stream and the notification's artwork are fetched through the app's authenticated
 * OkHttp client, since every MusicHoarder endpoint requires the bearer token.
 */
@OptIn(UnstableApi::class)
class PlaybackService : MediaSessionService() {
    private var mediaSession: MediaSession? = null

    override fun onCreate() {
        super.onCreate()
        val graph = (application as MusicHoarderApp).graph
        val dataSourceFactory: DataSource.Factory =
            DefaultDataSource.Factory(this, OkHttpDataSource.Factory(graph.httpClient))

        val player = ExoPlayer.Builder(this)
            .setMediaSourceFactory(DefaultMediaSourceFactory(dataSourceFactory))
            .setAudioAttributes(
                AudioAttributes.Builder()
                    .setUsage(C.USAGE_MEDIA)
                    .setContentType(C.AUDIO_CONTENT_TYPE_MUSIC)
                    .build(),
                /* handleAudioFocus = */ true,
            )
            // Pause when the headphones are yanked out, like every other music app.
            .setHandleAudioBecomingNoisy(true)
            .build()

        mediaSession = MediaSession.Builder(this, player)
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
        mediaSession?.run {
            player.release()
            release()
        }
        mediaSession = null
        super.onDestroy()
    }
}
