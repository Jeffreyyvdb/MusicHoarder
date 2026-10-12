package com.musichoarder.nativeaudio;

import android.content.Context;
import android.os.Handler;
import android.os.Looper;
import android.webkit.CookieManager;
import androidx.annotation.NonNull;
import androidx.annotation.Nullable;
import androidx.annotation.OptIn;
import androidx.media3.common.AudioAttributes;
import androidx.media3.common.C;
import androidx.media3.common.ForwardingPlayer;
import androidx.media3.common.MediaItem;
import androidx.media3.common.PlaybackException;
import androidx.media3.common.Player;
import androidx.media3.common.util.UnstableApi;
import androidx.media3.datasource.DataSource;
import androidx.media3.datasource.okhttp.OkHttpDataSource;
import androidx.media3.exoplayer.ExoPlayer;
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory;
import com.getcapacitor.JSArray;
import com.getcapacitor.JSObject;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import okhttp3.Interceptor;
import okhttp3.OkHttpClient;
import okhttp3.Request;

/**
 * The player behind the web app's transport: the Android twin of the iOS plugin's AudioEngine, and
 * the same protocol (the web side is frontend/src/lib/native/native-audio.ts).
 *
 * The web player stays the one that decides what plays: it loads a track, and hands over the
 * tracks that follow it. What this adds is that a song's end no longer needs the page. The player
 * moves to the next item by itself and reports where it got to (`trail`), so the page catches up
 * whenever it next runs. Main thread only.
 */
@OptIn(markerClass = UnstableApi.class)
final class AudioEngine implements Player.Listener {

    interface Listener {
        void onEngineEvent(String event, JSObject data);
    }

    /** How many tracks past the current one are kept in the player. */
    static final int MAX_UPCOMING = 20;
    /** How long the page has to pick up a notification command before it is carried out here. */
    private static final long REMOTE_ACK_TIMEOUT_MS = 500;
    private static final long TICK_MS = 1000;

    private final ExoPlayer player;
    private final Player sessionPlayer;
    private final DataSource.Factory dataSourceFactory;
    private final Handler handler = new Handler(Looper.getMainLooper());

    @Nullable
    private Listener listener;

    /** A track has been loaded and not stopped: there is something to report on. */
    private boolean loaded = false;
    private int loadId = 0;
    private int seq = 0;
    /** The URLs that started playing since the last load, in order; the last one is current. */
    private final ArrayList<String> trail = new ArrayList<>();
    /** The last item played to its end with nothing after it. */
    private boolean endedAtTail = false;
    /** The player was told to move on from here (a Next the page did not pick up). */
    private boolean advanceRequested = false;
    private int remoteId = 0;
    private final HashSet<Integer> pendingRemotes = new HashSet<>();

    private final Runnable tick = new Runnable() {
        @Override
        public void run() {
            emitState();
            if (player.isPlaying()) handler.postDelayed(this, TICK_MS);
        }
    };

    AudioEngine(Context context) {
        // Streams and covers are authorised by the web view's session cookie. A network
        // interceptor runs on every hop, so a redirect to another host is sent that host's
        // cookies (none), never the session.
        Interceptor cookies = chain -> {
            Request request = chain.request();
            String cookie = CookieManager.getInstance().getCookie(request.url().toString());
            Request.Builder builder = request.newBuilder().removeHeader("Cookie");
            if (cookie != null && !cookie.isEmpty()) builder.header("Cookie", cookie);
            return chain.proceed(builder.build());
        };
        OkHttpClient client = new OkHttpClient.Builder().addNetworkInterceptor(cookies).build();
        dataSourceFactory = new OkHttpDataSource.Factory(client);

        player = new ExoPlayer.Builder(context)
            .setMediaSourceFactory(new DefaultMediaSourceFactory(context).setDataSourceFactory(dataSourceFactory))
            .setAudioAttributes(
                new AudioAttributes.Builder().setUsage(C.USAGE_MEDIA).setContentType(C.AUDIO_CONTENT_TYPE_MUSIC).build(),
                /* handleAudioFocus= */true
            )
            // Pause when the headphones are yanked out, like every other music app.
            .setHandleAudioBecomingNoisy(true)
            .setWakeMode(C.WAKE_MODE_NETWORK)
            .build();
        player.addListener(this);
        sessionPlayer = new SessionPlayer(player);
    }

    /** The player as the media session sees it: its commands go to the page first. */
    Player sessionPlayer() {
        return sessionPlayer;
    }

    DataSource.Factory dataSourceFactory() {
        return dataSourceFactory;
    }

    void setListener(@Nullable Listener listener) {
        this.listener = listener;
    }

    @Nullable
    Listener listener() {
        return listener;
    }

    boolean isPlaying() {
        return loaded && player.getPlayWhenReady();
    }

    void release() {
        handler.removeCallbacksAndMessages(null);
        player.removeListener(this);
        player.release();
    }

    // What the page asks for

    /** Replace whatever is loaded with `track`, at `position`, playing or not. */
    void load(Track track, double position, boolean autoplay, int loadId, int seq) {
        this.loadId = loadId;
        this.seq = seq;
        trail.clear();
        trail.add(track.url);
        endedAtTail = false;
        advanceRequested = false;

        int index = player.getCurrentMediaItemIndex();
        boolean usable = loaded && player.getPlaybackState() != Player.STATE_IDLE && position < 0.01;
        loaded = true;
        if (usable && index + 1 < player.getMediaItemCount() && player.getMediaItemAt(index + 1).mediaId.equals(track.url)) {
            // The track asked for is the one already waiting behind the current one: step onto
            // it, buffered as it is, instead of loading it again.
            player.removeMediaItems(0, index + 1);
        } else if (
            usable &&
            index < player.getMediaItemCount() &&
            player.getMediaItemAt(index).mediaId.equals(track.url) &&
            player.getCurrentPosition() < 1500 &&
            player.getPlaybackState() != Player.STATE_ENDED
        ) {
            // Already there: the song before it ended a moment ago.
            if (index > 0) player.removeMediaItems(0, index);
        } else {
            player.setMediaItem(track.toMediaItem(), Math.round(position * 1000));
            player.prepare();
        }
        player.setPlayWhenReady(autoplay);
        emitState();
    }

    /** The tracks that follow the current one, in order. Sent again whenever the queue changes. */
    void setUpcoming(List<Track> tracks, int loadId) {
        if (!loaded || loadId != this.loadId || player.getMediaItemCount() == 0) return;
        int first = player.getCurrentMediaItemIndex() + 1;
        int count = player.getMediaItemCount();
        int wanted = Math.min(tracks.size(), MAX_UPCOMING);

        // Keep what already matches: the next item is usually buffered by now.
        int keep = 0;
        while (keep < wanted && first + keep < count && player.getMediaItemAt(first + keep).mediaId.equals(tracks.get(keep).url)) {
            keep++;
        }
        if (first + keep < count) player.removeMediaItems(first + keep, count);
        ArrayList<MediaItem> added = new ArrayList<>();
        for (int i = keep; i < wanted; i++) added.add(tracks.get(i).toMediaItem());
        if (!added.isEmpty()) player.addMediaItems(added);
    }

    /** New words for the track that is loaded (its library row arrived after it started). */
    void setMetadata(Track track, int loadId) {
        if (!loaded || loadId != this.loadId || player.getMediaItemCount() == 0) return;
        int index = player.getCurrentMediaItemIndex();
        if (player.getMediaItemAt(index).mediaId.equals(track.url)) {
            player.replaceMediaItem(index, track.toMediaItem());
        }
    }

    void play(int seq) {
        this.seq = seq;
        if (!loaded) return;
        if (player.getPlaybackState() == Player.STATE_IDLE) player.prepare();
        if (player.getPlaybackState() == Player.STATE_ENDED) {
            // Played out: Play starts the song over.
            endedAtTail = false;
            player.seekTo(0);
        }
        player.play();
        emitState();
    }

    void pause(int seq) {
        this.seq = seq;
        if (!loaded) return;
        player.pause();
        emitState();
    }

    void seek(double time, int seq) {
        this.seq = seq;
        if (!loaded) return;
        if (player.getPlaybackState() == Player.STATE_IDLE) player.prepare();
        endedAtTail = false;
        player.seekTo(Math.max(0, Math.round(time * 1000)));
        emitState();
    }

    void setRate(float rate) {
        player.setPlaybackSpeed(rate); // speed only: a slowed song keeps its pitch
    }

    void setVolume(float volume) {
        player.setVolume(volume);
    }

    void stop() {
        loadId += 1;
        loaded = false;
        endedAtTail = false;
        advanceRequested = false;
        trail.clear();
        pendingRemotes.clear();
        handler.removeCallbacks(tick);
        player.stop();
        player.clearMediaItems();
    }

    /** The page picked a notification command up, so it is not carried out here as well. */
    void acknowledgeRemote(int id) {
        pendingRemotes.remove(Integer.valueOf(id));
    }

    JSObject state() {
        long duration = player.getDuration();
        // "paused" is someone's doing (Pause, a call taking the audio focus, headphones coming
        // out). Anything else that keeps a wanted song silent is "waiting", which the page's
        // stall watchdog acts on.
        String status;
        if (
            !loaded ||
            !player.getPlayWhenReady() ||
            player.getPlaybackSuppressionReason() != Player.PLAYBACK_SUPPRESSION_REASON_NONE ||
            player.getPlaybackState() == Player.STATE_ENDED
        ) {
            status = "paused";
        } else {
            status = player.isPlaying() ? "playing" : "waiting";
        }
        JSArray urls = new JSArray();
        for (String url : trail) urls.put(url);

        JSObject state = new JSObject();
        state.put("loadId", loadId);
        state.put("seq", seq);
        state.put("index", Math.max(0, trail.size() - 1));
        state.put("trail", urls);
        state.put("url", trail.isEmpty() ? "" : trail.get(trail.size() - 1));
        state.put("status", status);
        state.put("position", Math.max(0, player.getCurrentPosition()) / 1000.0);
        state.put("duration", duration == C.TIME_UNSET || duration <= 0 ? 0 : duration / 1000.0);
        state.put("rate", (double) player.getPlaybackParameters().speed);
        state.put("ended", endedAtTail);
        return state;
    }

    // What the player reports

    @Override
    public void onMediaItemTransition(@Nullable MediaItem item, int reason) {
        if (!loaded || item == null) return;
        boolean byItself = reason == Player.MEDIA_ITEM_TRANSITION_REASON_AUTO;
        boolean asked = advanceRequested && reason == Player.MEDIA_ITEM_TRANSITION_REASON_SEEK;
        if (asked) advanceRequested = false;
        if (!byItself && !asked) return;

        trail.add(item.mediaId);
        // The current item stays first, so "what follows" is simply the rest of the list.
        int index = player.getCurrentMediaItemIndex();
        if (index > 0) player.removeMediaItems(0, index);
        emitState();
    }

    @Override
    public void onPlaybackStateChanged(int playbackState) {
        if (playbackState == Player.STATE_ENDED && loaded && !endedAtTail) {
            // Nothing follows in the queue. The page supplies what does (a station's next batch).
            endedAtTail = true;
            JSObject data = new JSObject();
            data.put("loadId", loadId);
            data.put("index", Math.max(0, trail.size() - 1));
            emit("ended", data);
        }
        emitState();
    }

    @Override
    public void onPlayerError(@NonNull PlaybackException error) {
        if (!loaded) return;
        // 3xxx are parsing errors and 4xxx decoding errors: the file, not the fetching of it.
        boolean format = error.errorCode >= 3000 && error.errorCode < 5000;
        JSObject data = new JSObject();
        data.put("loadId", loadId);
        data.put("index", Math.max(0, trail.size() - 1));
        data.put("url", trail.isEmpty() ? "" : trail.get(trail.size() - 1));
        data.put("message", error.getMessage() == null ? "Playback failed." : error.getMessage());
        data.put("kind", format ? "format" : "network");
        emit("error", data);
    }

    @Override
    public void onIsPlayingChanged(boolean isPlaying) {
        handler.removeCallbacks(tick);
        if (isPlaying) handler.postDelayed(tick, TICK_MS);
        emitState();
    }

    @Override
    public void onPlayWhenReadyChanged(boolean playWhenReady, int reason) {
        emitState();
    }

    @Override
    public void onPlaybackSuppressionReasonChanged(int playbackSuppressionReason) {
        emitState();
    }

    @Override
    public void onPositionDiscontinuity(@NonNull Player.PositionInfo oldPosition, @NonNull Player.PositionInfo newPosition, int reason) {
        if (reason == Player.DISCONTINUITY_REASON_SEEK) emitState();
    }

    private void emit(String event, JSObject data) {
        if (listener != null) listener.onEngineEvent(event, data);
    }

    private void emitState() {
        if (loaded) emit("state", state());
    }

    // The notification, the lock screen, headsets

    /**
     * Hand a command to the page, which knows what it means for the account's session; carry it
     * out here only when the page does not answer (it is asleep).
     */
    private void remote(String command, double time) {
        if (!loaded) return;
        final int id = ++remoteId;
        pendingRemotes.add(id);
        JSObject data = new JSObject();
        data.put("id", id);
        data.put("command", command);
        if (!Double.isNaN(time)) data.put("time", time);
        emit("remote", data);
        handler.postDelayed(
            () -> {
                if (pendingRemotes.remove(Integer.valueOf(id))) carryOut(command, time);
            },
            REMOTE_ACK_TIMEOUT_MS
        );
    }

    private void carryOut(String command, double time) {
        switch (command) {
            case "play":
                play(seq);
                break;
            case "pause":
                pause(seq);
                break;
            case "next":
                if (player.hasNextMediaItem()) {
                    advanceRequested = true;
                    player.seekToNextMediaItem();
                }
                break;
            case "previous":
                seek(0, seq);
                break;
            case "seek":
                if (!Double.isNaN(time)) seek(time, seq);
                break;
            default:
                break;
        }
    }

    /**
     * What the media session drives. Everything a controller can press is turned into a command
     * for the page; the engine itself uses the player underneath, so nothing it does on the
     * page's behalf comes back round as a press.
     */
    private final class SessionPlayer extends ForwardingPlayer {

        SessionPlayer(Player player) {
            super(player);
        }

        @Override
        public void play() {
            remote("play", Double.NaN);
        }

        @Override
        public void pause() {
            remote("pause", Double.NaN);
        }

        @Override
        public void setPlayWhenReady(boolean playWhenReady) {
            remote(playWhenReady ? "play" : "pause", Double.NaN);
        }

        @Override
        public void stop() {
            remote("pause", Double.NaN);
        }

        @Override
        public void seekToNext() {
            remote("next", Double.NaN);
        }

        @Override
        public void seekToNextMediaItem() {
            remote("next", Double.NaN);
        }

        @Override
        public void seekToPrevious() {
            remote("previous", Double.NaN);
        }

        @Override
        public void seekToPreviousMediaItem() {
            remote("previous", Double.NaN);
        }

        @Override
        public void seekTo(long positionMs) {
            remote("seek", positionMs / 1000.0);
        }

        @Override
        public void seekTo(int mediaItemIndex, long positionMs) {
            if (mediaItemIndex == getCurrentMediaItemIndex()) remote("seek", Math.max(0, positionMs) / 1000.0);
        }

        // Next and Previous are always offered: the page can supply a next track that is not in
        // the queue yet (a station), and Previous restarts the first one.
        @NonNull
        @Override
        public Commands getAvailableCommands() {
            return super
                .getAvailableCommands()
                .buildUpon()
                .add(COMMAND_SEEK_TO_NEXT)
                .add(COMMAND_SEEK_TO_NEXT_MEDIA_ITEM)
                .add(COMMAND_SEEK_TO_PREVIOUS)
                .add(COMMAND_SEEK_TO_PREVIOUS_MEDIA_ITEM)
                .build();
        }

        @Override
        public boolean isCommandAvailable(int command) {
            return getAvailableCommands().contains(command);
        }
    }
}
