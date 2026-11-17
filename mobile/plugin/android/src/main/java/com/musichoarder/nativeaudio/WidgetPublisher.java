package com.musichoarder.nativeaudio;

import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.os.Handler;
import android.os.Looper;
import androidx.annotation.Nullable;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import okhttp3.Request;
import okhttp3.Response;

/**
 * The widgets' only writer on this side of the app: the engine publishes what is playing, the
 * Widgets plugin the shelf, and every change re-renders the home screen widgets. Main thread
 * only, apart from the cover downloads, which hand their result back to it.
 *
 * Nothing here polls. The widgets are redrawn when something was written, and a write of equal
 * content is skipped, so the engine can report every player event without churn.
 */
final class WidgetPublisher {

    /** RemoteViews carries bitmaps through a binder with a hard budget, so a cover stays small. */
    static final int ART_PX = 256;
    private static final long MAX_COVER_BYTES = 8L * 1024 * 1024;

    interface CoverCallback {
        /** Main thread; null when the download or decode failed. */
        void onCover(@Nullable byte[] jpeg);
    }

    private static WidgetPublisher instance;

    private final Context context;
    private final WidgetStore store;
    private final Handler main = new Handler(Looper.getMainLooper());
    private final ExecutorService network = Executors.newFixedThreadPool(2);
    private final Set<String> fetching = new HashSet<>();
    private final Set<String> failed = new HashSet<>();

    /** The last thing the engine asked to publish, so a cover that arrives later can complete it. */
    @Nullable
    private Pending pending;

    private static final class Pending {

        final String title;
        final String artist;
        final String album;
        @Nullable
        final String artUrl;
        final WidgetSnapshot.Transport transport;

        Pending(String title, String artist, String album, @Nullable String artUrl, WidgetSnapshot.Transport transport) {
            this.title = title;
            this.artist = artist;
            this.album = album;
            this.artUrl = artUrl;
            this.transport = transport;
        }
    }

    static synchronized WidgetPublisher get(Context context) {
        if (instance == null) instance = new WidgetPublisher(context.getApplicationContext());
        return instance;
    }

    private WidgetPublisher(Context context) {
        this.context = context;
        this.store = new WidgetStore(new File(context.getFilesDir(), "widgets"));
    }

    WidgetStore store() {
        return store;
    }

    /** The engine's report: a live player, with what a button may do. */
    void publish(String title, String artist, String album, @Nullable String artUrl, WidgetSnapshot.Transport transport) {
        pending = new Pending(title, artist, album, artUrl, transport);
        String name = artUrl == null ? null : WidgetStore.artName(artUrl);
        boolean haveArt = name != null && store.hasArt(name);
        if (name != null && !haveArt) fetchArt(artUrl, name);
        write(new WidgetSnapshot.NowPlaying(title, artist, album, haveArt ? name : null, transport));
    }

    /** The track stays, the promise that a button can act does not. */
    void demote() {
        pending = null;
        write(store.nowPlaying().demoted());
    }

    /**
     * No engine in this process means nothing behind a "background" snapshot (the app was killed,
     * or this is a new process), whoever wrote it.
     */
    void reconcile() {
        if (PlaybackService.engine() == null) demote();
    }

    void clear() {
        pending = null;
        store.clear();
        renderAll();
    }

    void renderAll() {
        NowPlayingWidgetProvider.renderAll(context);
        LastPlayedWidgetProvider.renderAll(context);
    }

    private void write(WidgetSnapshot.NowPlaying value) {
        if (store.replaceNowPlaying(value)) renderAll();
    }

    private void fetchArt(String url, String name) {
        if (failed.contains(name) || !fetching.add(name)) return;
        fetchCover(url, jpeg -> {
            fetching.remove(name);
            if (jpeg == null) {
                failed.add(name);
                return;
            }
            if (!store.writeArt(name, jpeg)) return;
            Pending current = pending;
            if (current != null && url.equals(current.artUrl)) {
                publish(current.title, current.artist, current.album, current.artUrl, current.transport);
            }
        });
    }

    /** Download, centre-crop to a square of at most {@link #ART_PX} and encode, off the main thread. */
    void fetchCover(String url, CoverCallback callback) {
        network.execute(() -> {
            byte[] jpeg = null;
            try {
                jpeg = download(url);
            } catch (Exception | OutOfMemoryError ignored) {
                // a cover is decoration; the widget keeps its placeholder
            }
            byte[] result = jpeg;
            main.post(() -> callback.onCover(result));
        });
    }

    @Nullable
    private static byte[] download(String url) throws Exception {
        Request request = new Request.Builder().url(url).build();
        try (Response response = AuthorisedHttp.client().newCall(request).execute()) {
            if (!response.isSuccessful() || response.body() == null) return null;
            byte[] bytes = response.body().source().readByteArray();
            if (bytes.length > MAX_COVER_BYTES) return null;
            BitmapFactory.Options bounds = new BitmapFactory.Options();
            bounds.inJustDecodeBounds = true;
            BitmapFactory.decodeByteArray(bytes, 0, bytes.length, bounds);
            if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return null;
            BitmapFactory.Options options = new BitmapFactory.Options();
            int shortSide = Math.min(bounds.outWidth, bounds.outHeight);
            options.inSampleSize = 1;
            while (shortSide / (options.inSampleSize * 2) >= ART_PX) options.inSampleSize *= 2;
            Bitmap decoded = BitmapFactory.decodeByteArray(bytes, 0, bytes.length, options);
            if (decoded == null) return null;
            int side = Math.min(decoded.getWidth(), decoded.getHeight());
            Bitmap square = Bitmap.createBitmap(decoded, (decoded.getWidth() - side) / 2, (decoded.getHeight() - side) / 2, side, side);
            Bitmap scaled = side > ART_PX ? Bitmap.createScaledBitmap(square, ART_PX, ART_PX, true) : square;
            ByteArrayOutputStream out = new ByteArrayOutputStream();
            scaled.compress(Bitmap.CompressFormat.JPEG, 88, out);
            return out.toByteArray();
        }
    }

    /** Called by the plugin when it has landed the shelf's covers and replaced shelf.json. */
    void shelfChanged(List<WidgetSnapshot.ShelfItem> items) {
        if (store.replaceShelf(items)) renderAll();
    }
}
