package com.musichoarder.nativeaudio;

import android.app.PendingIntent;
import android.content.Intent;
import androidx.annotation.NonNull;
import androidx.annotation.Nullable;
import androidx.annotation.OptIn;
import androidx.media3.common.util.UnstableApi;
import androidx.media3.datasource.DataSourceBitmapLoader;
import androidx.media3.session.CacheBitmapLoader;
import androidx.media3.session.MediaSession;
import androidx.media3.session.MediaSessionService;

/**
 * Keeps playback alive outside the app: a foreground service with the system media notification,
 * lock-screen controls and Bluetooth/headset keys. It owns the engine; the plugin reaches it
 * through {@link #engine()} once a controller has brought the service up.
 */
@OptIn(markerClass = UnstableApi.class)
public class PlaybackService extends MediaSessionService {

    @Nullable
    private static AudioEngine engine;

    @Nullable
    private MediaSession session;

    /** The running service's engine, or null while the service is down. Main thread only. */
    @Nullable
    static AudioEngine engine() {
        return engine;
    }

    @Override
    public void onCreate() {
        super.onCreate();
        AudioEngine created = new AudioEngine(this);
        engine = created;

        MediaSession.Builder builder = new MediaSession.Builder(this, created.sessionPlayer())
            // Covers come from the same server as the streams, behind the same cookie.
            .setBitmapLoader(
                new CacheBitmapLoader(
                    new DataSourceBitmapLoader(DataSourceBitmapLoader.DEFAULT_EXECUTOR_SERVICE.get(), created.dataSourceFactory())
                )
            );
        Intent launch = getPackageManager().getLaunchIntentForPackage(getPackageName());
        if (launch != null) {
            builder.setSessionActivity(
                PendingIntent.getActivity(this, 0, launch, PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT)
            );
        }
        session = builder.build();
    }

    @Nullable
    @Override
    public MediaSession onGetSession(@NonNull MediaSession.ControllerInfo controllerInfo) {
        return session;
    }

    /**
     * Swiping the app away takes the page with it, and the page is what knows the queue. Playing
     * on without it would leave music nobody can steer, so the player stops with the app.
     */
    @Override
    public void onTaskRemoved(@Nullable Intent rootIntent) {
        AudioEngine current = engine;
        if (current != null) current.stop();
        stopSelf();
    }

    @Override
    public void onDestroy() {
        if (session != null) {
            session.release();
            session = null;
        }
        AudioEngine current = engine;
        engine = null;
        if (current != null) current.release();
        super.onDestroy();
    }
}
