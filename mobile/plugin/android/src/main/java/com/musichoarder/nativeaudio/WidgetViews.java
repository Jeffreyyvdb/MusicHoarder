package com.musichoarder.nativeaudio;

import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.graphics.PorterDuff;
import android.graphics.PorterDuffXfermode;
import android.graphics.RectF;
import android.view.View;
import android.widget.RemoteViews;
import androidx.annotation.Nullable;
import java.io.File;
import java.util.List;

/** What the two widgets look like and what their taps do, drawn from the stored documents. */
final class WidgetViews {

    /** The extra on MainActivity's intent: `now-playing`, `resume` or `shelf/<id>`. */
    static final String EXTRA_ACTION = "mh.widget";
    static final String ACTION_COMMAND = "com.musichoarder.nativeaudio.WIDGET_COMMAND";
    static final String EXTRA_COMMAND = "command";

    private static final int[] CELLS = { R.id.mh_lp_cell0, R.id.mh_lp_cell1, R.id.mh_lp_cell2, R.id.mh_lp_cell3 };
    private static final int[] COVERS = { R.id.mh_lp_cover0, R.id.mh_lp_cover1, R.id.mh_lp_cover2, R.id.mh_lp_cover3 };
    private static final int[] TITLES = { R.id.mh_lp_title0, R.id.mh_lp_title1, R.id.mh_lp_title2, R.id.mh_lp_title3 };

    private WidgetViews() {}

    static RemoteViews nowPlaying(Context context, WidgetStore store, WidgetSnapshot.NowPlaying now, boolean wide) {
        RemoteViews views = new RemoteViews(context.getPackageName(), wide ? R.layout.mh_widget_now_playing_wide : R.layout.mh_widget_now_playing_small);
        views.setImageViewResource(R.id.mh_np_icon, context.getApplicationInfo().icon);
        WidgetSnapshot.Transport transport = now.transport;

        if (now.isNothing()) {
            views.setTextViewText(R.id.mh_np_title, context.getString(R.string.mh_widget_nothing_playing));
            views.setTextViewText(R.id.mh_np_artist, context.getString(R.string.mh_widget_open_app));
            views.setImageViewResource(R.id.mh_np_cover, R.drawable.mh_widget_cover_placeholder);
            views.setViewVisibility(R.id.mh_np_play, View.GONE);
            if (wide) {
                views.setViewVisibility(R.id.mh_np_previous, View.GONE);
                views.setViewVisibility(R.id.mh_np_next, View.GONE);
            }
            views.setOnClickPendingIntent(R.id.mh_np_root, launch(context, null));
            return views;
        }

        views.setTextViewText(R.id.mh_np_title, now.title);
        views.setTextViewText(R.id.mh_np_artist, now.artist);
        Bitmap cover = cover(store, now.art, WidgetPublisher.ART_PX);
        if (cover != null) views.setImageViewBitmap(R.id.mh_np_cover, cover);
        else views.setImageViewResource(R.id.mh_np_cover, R.drawable.mh_widget_cover_placeholder);

        views.setOnClickPendingIntent(R.id.mh_np_root, launch(context, "now-playing"));
        views.setViewVisibility(R.id.mh_np_play, View.VISIBLE);
        // A button carries what its glyph shows, never a toggle: a stale widget must not pause what it shows as paused.
        boolean playing = transport != null && transport.playing;
        views.setImageViewResource(R.id.mh_np_play, playing ? R.drawable.mh_widget_ic_pause : R.drawable.mh_widget_ic_play);
        views.setContentDescription(R.id.mh_np_play, context.getString(playing ? R.string.mh_widget_pause : R.string.mh_widget_play));
        views.setOnClickPendingIntent(R.id.mh_np_play, transport == null ? launch(context, "resume") : command(context, playing ? "pause" : "play"));

        if (wide) {
            boolean previous = transport != null && transport.canPrevious;
            boolean next = transport != null && transport.canNext;
            views.setViewVisibility(R.id.mh_np_previous, previous ? View.VISIBLE : View.GONE);
            views.setViewVisibility(R.id.mh_np_next, next ? View.VISIBLE : View.GONE);
            if (previous) views.setOnClickPendingIntent(R.id.mh_np_previous, command(context, "previous"));
            if (next) views.setOnClickPendingIntent(R.id.mh_np_next, command(context, "next"));
        }
        return views;
    }

    static RemoteViews lastPlayed(Context context, WidgetStore store, List<WidgetSnapshot.ShelfItem> shelf, boolean tall) {
        RemoteViews views = new RemoteViews(context.getPackageName(), R.layout.mh_widget_last_played);
        views.setOnClickPendingIntent(R.id.mh_lp_root, launch(context, null));
        boolean any = !shelf.isEmpty();
        views.setViewVisibility(R.id.mh_lp_empty, any ? View.GONE : View.VISIBLE);
        views.setViewVisibility(R.id.mh_lp_cells, any ? View.VISIBLE : View.GONE);
        for (int i = 0; i < CELLS.length; i++) {
            boolean present = i < shelf.size();
            views.setViewVisibility(CELLS[i], present ? View.VISIBLE : View.INVISIBLE);
            if (!present) continue;
            WidgetSnapshot.ShelfItem item = shelf.get(i);
            Bitmap cover = cover(store, item.art, 160);
            if (cover != null) views.setImageViewBitmap(COVERS[i], cover);
            else views.setImageViewResource(COVERS[i], R.drawable.mh_widget_cover_placeholder);
            views.setTextViewText(TITLES[i], item.title);
            views.setViewVisibility(TITLES[i], tall ? View.VISIBLE : View.GONE);
            views.setOnClickPendingIntent(CELLS[i], launch(context, "shelf/" + item.id));
        }
        return views;
    }

    /** A cover as a square with rounded corners, or null when its file is not there (yet). */
    @Nullable
    private static Bitmap cover(WidgetStore store, @Nullable String name, int px) {
        File file = store.artFile(name);
        if (file == null || !file.isFile()) return null;
        Bitmap source = BitmapFactory.decodeFile(file.getPath());
        if (source == null) return null;
        Bitmap square = source.getWidth() == px ? source : Bitmap.createScaledBitmap(source, px, px, true);
        Bitmap rounded = Bitmap.createBitmap(px, px, Bitmap.Config.ARGB_8888);
        Canvas canvas = new Canvas(rounded);
        Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
        float radius = px * 0.14f;
        canvas.drawRoundRect(new RectF(0, 0, px, px), radius, radius, paint);
        paint.setXfermode(new PorterDuffXfermode(PorterDuff.Mode.SRC_IN));
        canvas.drawBitmap(square, 0, 0, paint);
        return rounded;
    }

    /** A press on a button the player can act on: a broadcast only this app can send. */
    static PendingIntent command(Context context, String command) {
        Intent intent = new Intent(context, WidgetCommandReceiver.class).setAction(ACTION_COMMAND).putExtra(EXTRA_COMMAND, command);
        return PendingIntent.getBroadcast(context, command.hashCode(), intent, PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
    }

    /** The launcher activity, explicitly (no exported filter), carrying what the page should do. */
    @Nullable
    static PendingIntent launch(Context context, @Nullable String action) {
        Intent intent = context.getPackageManager().getLaunchIntentForPackage(context.getPackageName());
        if (intent == null) return null;
        if (action != null) intent.putExtra(EXTRA_ACTION, action);
        // Extras do not tell two PendingIntents apart, so each action gets a code of its own.
        int code = action == null ? 0 : action.hashCode();
        return PendingIntent.getActivity(context, code, intent, PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
    }
}
