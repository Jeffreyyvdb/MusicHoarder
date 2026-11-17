package com.musichoarder.nativeaudio;

import androidx.annotation.Nullable;
import java.util.List;

/**
 * What a tap on a widget asks the page to do. The widget names a closed set of targets; the app
 * never builds a server URL from one, and never reloads the web view for it. The page pulls the
 * action and navigates in place, after its own route guard.
 */
final class LaunchAction {

    enum Kind {
        NOW_PLAYING,
        RESUME,
        OPEN,
    }

    /** An action older than this is dropped: it must never start music long after the tap. */
    static final long MAX_AGE_MS = 30_000;

    final Kind kind;

    /** Set for {@link Kind#OPEN}. */
    @Nullable
    final String path;

    private LaunchAction(Kind kind, @Nullable String path) {
        this.kind = kind;
        this.path = path;
    }

    /** `now-playing`, `resume`, or `shelf/<id>` (resolved to that item's path); anything else is null. */
    @Nullable
    static LaunchAction parse(@Nullable String raw, List<WidgetSnapshot.ShelfItem> shelf) {
        if (raw == null) return null;
        if (raw.equals("now-playing")) return new LaunchAction(Kind.NOW_PLAYING, null);
        if (raw.equals("resume")) return new LaunchAction(Kind.RESUME, null);
        if (raw.startsWith("shelf/")) {
            String id = raw.substring("shelf/".length());
            for (WidgetSnapshot.ShelfItem item : shelf) {
                if (item.id.equals(id)) return new LaunchAction(Kind.OPEN, item.path);
            }
        }
        return null;
    }

    /** The latest action, held until the page asks for it. */
    static final class Mailbox {

        @Nullable
        private LaunchAction held;
        private long heldAt;

        synchronized void put(LaunchAction action, long now) {
            held = action;
            heldAt = now;
        }

        /** A destructive read: the action once, and only while it is fresh. */
        @Nullable
        synchronized LaunchAction take(long now) {
            LaunchAction action = held;
            held = null;
            return action != null && now - heldAt <= MAX_AGE_MS ? action : null;
        }
    }
}
