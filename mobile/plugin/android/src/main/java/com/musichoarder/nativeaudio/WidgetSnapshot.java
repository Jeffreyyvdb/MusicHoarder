package com.musichoarder.nativeaudio;

import androidx.annotation.Nullable;
import java.util.ArrayList;
import java.util.List;
import java.util.Objects;
import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

/**
 * What the widgets draw, as the two JSON documents of the widget contract (the iOS twin reads the
 * same shapes). Immutable, and the one place that knows the keys. No Android types, so it runs
 * under plain JUnit.
 */
final class WidgetSnapshot {

    static final int VERSION = 1;
    static final int MAX_SHELF = 8;

    private WidgetSnapshot() {}

    /** Can a button act without the app in front? */
    enum Reach {
        /** A live engine published this: buttons are broadcasts to WidgetCommandReceiver. */
        BACKGROUND,
        /** No engine holds it (swiped away, new process): buttons open the app. */
        NEEDS_APP,
    }

    /** What a live engine knows. Its presence is what makes a snapshot {@link Reach#BACKGROUND}. */
    static final class Transport {

        final boolean playing;
        final double position;
        final double duration;
        final double rate;
        final long asOf;
        final boolean canNext;
        final boolean canPrevious;

        Transport(boolean playing, double position, double duration, double rate, long asOf, boolean canNext, boolean canPrevious) {
            this.playing = playing;
            this.position = position;
            this.duration = duration;
            this.rate = rate;
            this.asOf = asOf;
            this.canNext = canNext;
            this.canPrevious = canPrevious;
        }
    }

    static final class NowPlaying {

        static final NowPlaying NOTHING = new NowPlaying(null, "", "", null, null);

        /** Null: nothing loaded, whatever the other fields say. */
        @Nullable
        final String title;
        final String artist;
        final String album;
        /** A file name under widgets/art/, never a URL. */
        @Nullable
        final String art;
        /** Null exactly when the buttons cannot act without the app. */
        @Nullable
        final Transport transport;

        NowPlaying(@Nullable String title, String artist, String album, @Nullable String art, @Nullable Transport transport) {
            boolean empty = title == null || title.isEmpty();
            this.title = empty ? null : title;
            this.artist = empty ? "" : artist;
            this.album = empty ? "" : album;
            this.art = empty ? null : art;
            this.transport = empty ? null : transport;
        }

        boolean isNothing() {
            return title == null;
        }

        Reach reach() {
            return transport == null ? Reach.NEEDS_APP : Reach.BACKGROUND;
        }

        /** The same track, without the promise that a button can act. */
        NowPlaying demoted() {
            return transport == null ? this : new NowPlaying(title, artist, album, art, null);
        }

        /**
         * What a rewrite has to differ in. The clock and the position are left out on purpose:
         * they change with every event, and a widget that draws neither has nothing to redraw.
         */
        boolean sameAs(NowPlaying other) {
            if (!(Objects.equals(title, other.title) && artist.equals(other.artist) && album.equals(other.album))) return false;
            if (!Objects.equals(art, other.art)) return false;
            if (transport == null || other.transport == null) return transport == other.transport;
            Transport a = transport;
            Transport b = other.transport;
            return (
                a.playing == b.playing && a.canNext == b.canNext && a.canPrevious == b.canPrevious && a.duration == b.duration && a.rate == b.rate
            );
        }

        String toJson() {
            try {
                JSONObject json = new JSONObject().put("v", VERSION);
                if (isNothing()) return json.put("state", "nothing").toString();
                json.put("state", "track").put("title", title).put("artist", artist).put("album", album);
                json.put("art", art == null ? JSONObject.NULL : art);
                json.put("reach", reach() == Reach.BACKGROUND ? "background" : "needsApp");
                if (transport != null) {
                    json.put(
                        "transport",
                        new JSONObject()
                            .put("playing", transport.playing)
                            .put("position", transport.position)
                            .put("duration", transport.duration)
                            .put("rate", transport.rate)
                            .put("asOf", transport.asOf)
                            .put("canNext", transport.canNext)
                            .put("canPrevious", transport.canPrevious)
                    );
                }
                return json.toString();
            } catch (JSONException e) {
                throw new IllegalStateException(e); // only a non-finite number gets here
            }
        }

        /** NOTHING when the text is missing, unparsable, or of a version this build does not know. */
        static NowPlaying fromJson(@Nullable String text) {
            if (text == null) return NOTHING;
            try {
                JSONObject json = new JSONObject(text);
                if (json.optInt("v", -1) != VERSION || !"track".equals(json.optString("state"))) return NOTHING;
                String title = json.optString("title", "");
                if (title.isEmpty()) return NOTHING;
                String art = json.isNull("art") ? null : json.optString("art", null);
                if (art != null && !isArtName(art)) art = null;
                JSONObject t = json.optJSONObject("transport");
                // A "background" claim without a transport is a torn file, not a promise.
                Transport transport = null;
                if ("background".equals(json.optString("reach")) && t != null) {
                    transport = new Transport(
                        t.optBoolean("playing", false),
                        t.optDouble("position", 0),
                        t.optDouble("duration", 0),
                        t.optDouble("rate", 1),
                        t.optLong("asOf", 0),
                        t.optBoolean("canNext", false),
                        t.optBoolean("canPrevious", false)
                    );
                }
                return new NowPlaying(title, json.optString("artist", ""), json.optString("album", ""), art, transport);
            } catch (JSONException e) {
                return NOTHING;
            }
        }
    }

    static final class ShelfItem {

        final String id;
        final String title;
        final String subtitle;

        @Nullable
        final String art;
        /** The in-app path the page published for it. */
        final String path;

        ShelfItem(String id, String title, String subtitle, @Nullable String art, String path) {
            this.id = id;
            this.title = title;
            this.subtitle = subtitle;
            this.art = art;
            this.path = path;
        }

        @Override
        public boolean equals(Object other) {
            if (!(other instanceof ShelfItem)) return false;
            ShelfItem o = (ShelfItem) other;
            return id.equals(o.id) && title.equals(o.title) && subtitle.equals(o.subtitle) && Objects.equals(art, o.art) && path.equals(o.path);
        }

        @Override
        public int hashCode() {
            return Objects.hash(id, title, subtitle, art, path);
        }
    }

    static String shelfToJson(List<ShelfItem> items) {
        try {
            JSONArray array = new JSONArray();
            for (ShelfItem item : items) {
                array.put(
                    new JSONObject()
                        .put("id", item.id)
                        .put("title", item.title)
                        .put("subtitle", item.subtitle)
                        .put("art", item.art == null ? JSONObject.NULL : item.art)
                        .put("path", item.path)
                );
            }
            return new JSONObject().put("v", VERSION).put("items", array).toString();
        } catch (JSONException e) {
            throw new IllegalStateException(e);
        }
    }

    /** Empty when the text is missing, unparsable, or of an unknown version; at most {@link #MAX_SHELF} items. */
    static List<ShelfItem> shelfFromJson(@Nullable String text) {
        List<ShelfItem> items = new ArrayList<>();
        if (text == null) return items;
        try {
            JSONObject json = new JSONObject(text);
            JSONArray array = json.optJSONArray("items");
            if (json.optInt("v", -1) != VERSION || array == null) return items;
            for (int i = 0; i < array.length() && items.size() < MAX_SHELF; i++) {
                JSONObject o = array.optJSONObject(i);
                if (o == null) continue;
                String id = o.optString("id", "");
                String path = o.optString("path", "");
                if (id.isEmpty() || !isAppPath(path)) continue;
                String art = o.isNull("art") ? null : o.optString("art", null);
                if (art != null && !isArtName(art)) art = null;
                items.add(new ShelfItem(id, o.optString("title", ""), o.optString("subtitle", ""), art, path));
            }
        } catch (JSONException e) {
            items.clear();
        }
        return items;
    }

    /** A path inside the app: one leading slash, so never `//host` and never a scheme. */
    static boolean isAppPath(@Nullable String path) {
        return path != null && path.startsWith("/") && !path.startsWith("//") && path.indexOf('\\') < 0 && path.indexOf('\n') < 0;
    }

    /** `art/<64 hex>.jpg`: the only shape the reader will turn into a file path. */
    static boolean isArtName(String name) {
        return name.matches("art/[0-9a-f]{64}\\.jpg");
    }
}
