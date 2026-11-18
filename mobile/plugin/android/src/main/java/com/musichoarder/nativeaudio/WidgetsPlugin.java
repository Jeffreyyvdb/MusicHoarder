package com.musichoarder.nativeaudio;

import android.content.Context;
import android.content.Intent;
import android.os.Handler;
import android.os.Looper;
import androidx.annotation.Nullable;
import com.getcapacitor.JSArray;
import com.getcapacitor.JSObject;
import com.getcapacitor.Plugin;
import com.getcapacitor.PluginCall;
import com.getcapacitor.PluginMethod;
import com.getcapacitor.annotation.CapacitorPlugin;
import java.lang.ref.WeakReference;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import org.json.JSONObject;

/**
 * The page's side of the Home Screen widgets: it sends the shelf the Last played widget shows,
 * and takes the action a tap on a widget asked for. Now Playing needs nothing from the page, since
 * the engine publishes it. Frontend: frontend/src/lib/native/widgets.ts; the iOS twin is
 * WidgetsPlugin.swift, and both implement the same contract.
 */
@CapacitorPlugin(name = "Widgets")
public class WidgetsPlugin extends Plugin {

    private static final LaunchAction.Mailbox MAILBOX = new LaunchAction.Mailbox();
    private static final Handler MAIN = new Handler(Looper.getMainLooper());

    @Nullable
    private static WeakReference<WidgetsPlugin> live;

    /** A newer setShelf supersedes an older one still downloading its covers. */
    private int generation = 0;

    @Override
    public void load() {
        live = new WeakReference<>(this);
        // A new process holds no player, so a Now Playing button written by an earlier one must not promise to act.
        MAIN.post(() -> {
            WidgetPublisher publisher = WidgetPublisher.get(getContext());
            publisher.reconcile();
            publisher.renderAll();
        });
    }

    @Override
    protected void handleOnDestroy() {
        if (live != null && live.get() == this) live = null;
        generation++;
    }

    /**
     * `items`: at most 8 of `{ id, title, subtitle, artwork: absoluteUrl | null, path }`. Resolves
     * once accepted; the covers download afterwards and the widget redraws when they are in.
     */
    @PluginMethod
    public void setShelf(PluginCall call) {
        JSArray raw = call.getArray("items", new JSArray());
        List<Item> accepted = new ArrayList<>();
        for (int i = 0; i < raw.length() && accepted.size() < WidgetSnapshot.MAX_SHELF; i++) {
            JSONObject o = raw.optJSONObject(i);
            if (o == null) continue;
            String id = o.optString("id", "");
            String path = o.optString("path", "");
            if (id.isEmpty() || !WidgetSnapshot.isAppPath(path)) continue;
            String artwork = o.isNull("artwork") ? "" : o.optString("artwork", "");
            boolean http = artwork.startsWith("https://") || artwork.startsWith("http://");
            accepted.add(new Item(id, o.optString("title", ""), o.optString("subtitle", ""), http ? artwork : null, path));
        }
        call.resolve();

        int mine = ++generation;
        MAIN.post(() -> download(accepted, mine));
    }

    @PluginMethod
    public void clear(PluginCall call) {
        generation++;
        MAIN.post(() -> WidgetPublisher.get(getContext()).clear());
        call.resolve();
    }

    /** `{ action: null | { kind: 'now-playing' } | { kind: 'resume' } | { kind: 'open', path } }`, once. */
    @PluginMethod
    public void takeLaunchAction(PluginCall call) {
        LaunchAction action = MAILBOX.take(System.currentTimeMillis());
        JSObject result = new JSObject();
        if (action == null) {
            result.put("action", JSONObject.NULL);
        } else {
            JSObject body = new JSObject();
            switch (action.kind) {
                case NOW_PLAYING:
                    body.put("kind", "now-playing");
                    break;
                case RESUME:
                    body.put("kind", "resume");
                    break;
                default:
                    body.put("kind", "open");
                    body.put("path", action.path);
                    break;
            }
            result.put("action", body);
        }
        call.resolve(result);
    }

    /** Whether the widgets have a folder to read from: always, here, since they share the app's own files. */
    @PluginMethod
    public void status(PluginCall call) {
        JSObject result = new JSObject();
        result.put("sharedContainer", true);
        call.resolve(result);
    }

    /**
     * MainActivity's hand-off for the intent a widget tap started it with. Native never builds a
     * server URL from it: the action goes in a mailbox, and the page pulls it and navigates in place.
     */
    public static void deliverLaunch(Context context, @Nullable Intent intent) {
        if (intent == null) return;
        String raw = intent.getStringExtra(WidgetViews.EXTRA_ACTION);
        if (raw == null) return;
        intent.removeExtra(WidgetViews.EXTRA_ACTION); // spent: a recreated activity must not replay it
        LaunchAction action = LaunchAction.parse(raw, WidgetPublisher.get(context).store().shelf());
        if (action == null) return;
        MAILBOX.put(action, System.currentTimeMillis());
        WidgetsPlugin plugin = live == null ? null : live.get();
        if (plugin != null) plugin.notifyListeners("launchAction", new JSObject());
    }

    private static final class Item {

        final String id;
        final String title;
        final String subtitle;
        @Nullable
        final String artwork;
        final String path;

        Item(String id, String title, String subtitle, @Nullable String artwork, String path) {
            this.id = id;
            this.title = title;
            this.subtitle = subtitle;
            this.artwork = artwork;
            this.path = path;
        }
    }

    /** Fetch every cover not already on disk, then land them and the shelf together. */
    private void download(List<Item> items, int mine) {
        WidgetPublisher publisher = WidgetPublisher.get(getContext());
        Map<String, byte[]> landed = new HashMap<>();
        List<String> wanted = new ArrayList<>();
        for (Item item : items) {
            if (item.artwork == null) continue;
            String name = WidgetStore.artName(item.artwork);
            if (!publisher.store().hasArt(name) && !wanted.contains(item.artwork)) wanted.add(item.artwork);
        }
        if (wanted.isEmpty()) {
            finish(items, mine, landed);
            return;
        }
        int[] left = { wanted.size() };
        for (String url : wanted) {
            publisher.fetchCover(url, jpeg -> {
                if (jpeg != null) landed.put(url, jpeg);
                if (--left[0] == 0) finish(items, mine, landed);
            });
        }
    }

    private void finish(List<Item> items, int mine, Map<String, byte[]> landed) {
        if (mine != generation) return;
        WidgetPublisher publisher = WidgetPublisher.get(getContext());
        WidgetStore store = publisher.store();
        List<WidgetSnapshot.ShelfItem> shelf = new ArrayList<>();
        for (Item item : items) {
            String art = null;
            if (item.artwork != null) {
                String name = WidgetStore.artName(item.artwork);
                byte[] jpeg = landed.get(item.artwork);
                // A cover lands before the JSON that names it.
                if (store.hasArt(name) || (jpeg != null && store.writeArt(name, jpeg))) art = name;
            }
            shelf.add(new WidgetSnapshot.ShelfItem(item.id, item.title, item.subtitle, art, item.path));
        }
        publisher.shelfChanged(shelf);
    }
}
