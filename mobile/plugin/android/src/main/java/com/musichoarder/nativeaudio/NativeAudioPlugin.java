package com.musichoarder.nativeaudio;

import android.content.ComponentName;
import android.content.Context;
import android.os.Handler;
import android.os.Looper;
import androidx.annotation.Nullable;
import androidx.core.content.ContextCompat;
import androidx.media3.session.MediaController;
import androidx.media3.session.SessionToken;
import com.getcapacitor.JSArray;
import com.getcapacitor.JSObject;
import com.getcapacitor.Plugin;
import com.getcapacitor.PluginCall;
import com.getcapacitor.PluginMethod;
import com.getcapacitor.annotation.CapacitorPlugin;
import com.google.common.util.concurrent.ListenableFuture;
import java.util.ArrayList;
import java.util.List;
import org.json.JSONObject;

/**
 * The bridge between the web player and {@link AudioEngine}. The web side is
 * frontend/src/lib/native/native-audio.ts; this, the iOS plugin and that file are one protocol and
 * change together.
 */
@CapacitorPlugin(name = "NativeAudio")
public class NativeAudioPlugin extends Plugin implements AudioEngine.Listener {

    private interface EngineAction {
        void run(AudioEngine engine);
    }

    private final Handler main = new Handler(Looper.getMainLooper());
    /** What holds the playback service up while the page is here. */
    @Nullable
    private ListenableFuture<MediaController> controller;
    private final List<EngineAction> waiting = new ArrayList<>();
    // Set before the first track is loaded, so kept for the engine that does not exist yet.
    private float volume = 1f;
    private float rate = 1f;

    @Override
    public void onEngineEvent(String event, JSObject data) {
        notifyListeners(event, data);
    }

    @PluginMethod
    public void loadTrack(PluginCall call) {
        Track track = track(call.getData());
        if (track == null) {
            call.reject("A track needs an http(s) url.");
            return;
        }
        double position = call.getDouble("position", 0.0);
        boolean autoplay = call.getBoolean("autoplay", false);
        int loadId = call.getInt("loadId", 0);
        int seq = call.getInt("seq", 0);
        // The one call that brings the service up: nothing else has anything to play.
        withEngine(call, true, engine -> engine.load(track, position, autoplay, loadId, seq));
    }

    @PluginMethod
    public void setUpcoming(PluginCall call) {
        JSArray items = call.getArray("items", new JSArray());
        List<Track> tracks = new ArrayList<>();
        for (int i = 0; i < items.length(); i++) {
            Track track = track(items.optJSONObject(i));
            if (track != null) tracks.add(track);
        }
        int loadId = call.getInt("loadId", 0);
        withEngine(call, false, engine -> engine.setUpcoming(tracks, loadId));
    }

    @PluginMethod
    public void setMetadata(PluginCall call) {
        Track track = track(call.getData());
        if (track == null) {
            call.reject("A track needs an http(s) url.");
            return;
        }
        int loadId = call.getInt("loadId", 0);
        withEngine(call, false, engine -> engine.setMetadata(track, loadId));
    }

    @PluginMethod
    public void play(PluginCall call) {
        int seq = call.getInt("seq", 0);
        withEngine(call, false, engine -> engine.play(seq));
    }

    @PluginMethod
    public void pause(PluginCall call) {
        int seq = call.getInt("seq", 0);
        withEngine(call, false, engine -> engine.pause(seq));
    }

    @PluginMethod
    public void seek(PluginCall call) {
        double time = call.getDouble("time", 0.0);
        int seq = call.getInt("seq", 0);
        withEngine(call, false, engine -> engine.seek(time, seq));
    }

    @PluginMethod
    public void setRate(PluginCall call) {
        float value = call.getFloat("rate", 1f);
        main.post(() -> rate = value);
        withEngine(call, false, engine -> engine.setRate(value));
    }

    @PluginMethod
    public void setVolume(PluginCall call) {
        float value = call.getFloat("volume", 1f);
        main.post(() -> volume = value);
        withEngine(call, false, engine -> engine.setVolume(value));
    }

    @PluginMethod
    public void stop(PluginCall call) {
        main.post(waiting::clear);
        withEngine(call, false, AudioEngine::stop);
    }

    /** The notification always offers Next and Previous here; see AudioEngine.SessionPlayer. */
    @PluginMethod
    public void setCommands(PluginCall call) {
        call.resolve();
    }

    @PluginMethod
    public void ackRemote(PluginCall call) {
        int id = call.getInt("id", 0);
        withEngine(call, false, engine -> engine.acknowledgeRemote(id));
    }

    @PluginMethod
    public void getState(PluginCall call) {
        main.post(() -> {
            AudioEngine engine = PlaybackService.engine();
            call.resolve(engine == null ? new JSObject() : engine.state());
        });
    }

    /** Android's output switcher lives on the media notification; there is nothing to open. */
    @PluginMethod
    public void showRoutePicker(PluginCall call) {
        call.resolve();
    }

    @Override
    protected void handleOnDestroy() {
        main.post(() -> {
            waiting.clear();
            AudioEngine engine = PlaybackService.engine();
            if (engine != null && engine.listener() == this) engine.setListener(null);
            if (controller != null) {
                MediaController.releaseFuture(controller);
                controller = null;
            }
        });
    }

    /**
     * Run `action` on the engine, on the main thread. Without a running service it is dropped,
     * unless `start` says this is worth bringing the service up for; it then runs once it is.
     */
    private void withEngine(PluginCall call, boolean start, EngineAction action) {
        main.post(() -> {
            AudioEngine engine = PlaybackService.engine();
            if (engine != null && waiting.isEmpty()) {
                engine.setListener(this);
                action.run(engine);
            } else if (start || !waiting.isEmpty()) {
                // Behind a load that is waiting for the service, so the order holds.
                waiting.add(action);
                if (start) connect();
            }
            call.resolve();
        });
    }

    private void connect() {
        Context context = getContext().getApplicationContext();
        if (controller != null) {
            if (!controller.isDone() || PlaybackService.engine() != null) return;
            // The service went down under the old connection (the app was swiped away).
            MediaController.releaseFuture(controller);
        }
        SessionToken token = new SessionToken(context, new ComponentName(context, PlaybackService.class));
        ListenableFuture<MediaController> future = new MediaController.Builder(context, token).buildAsync();
        controller = future;
        future.addListener(
            () -> {
                AudioEngine engine = PlaybackService.engine();
                if (engine == null || controller != future) {
                    waiting.clear();
                    return;
                }
                engine.setListener(this);
                engine.setVolume(volume);
                engine.setRate(rate);
                List<EngineAction> actions = new ArrayList<>(waiting);
                waiting.clear();
                for (EngineAction action : actions) action.run(engine);
            },
            ContextCompat.getMainExecutor(context)
        );
    }

    @Nullable
    private static Track track(@Nullable JSONObject data) {
        if (data == null) return null;
        String url = data.optString("url", "");
        if (!url.startsWith("https://") && !url.startsWith("http://")) return null;
        String artwork = data.isNull("artwork") ? null : data.optString("artwork", null);
        return new Track(
            url,
            data.optString("title", ""),
            data.optString("artist", ""),
            data.optString("album", ""),
            artwork == null || artwork.isEmpty() ? null : artwork
        );
    }
}
