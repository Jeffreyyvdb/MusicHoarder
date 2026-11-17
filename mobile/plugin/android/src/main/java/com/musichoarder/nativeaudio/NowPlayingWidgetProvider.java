package com.musichoarder.nativeaudio;

import android.appwidget.AppWidgetManager;
import android.appwidget.AppWidgetProvider;
import android.content.ComponentName;
import android.content.Context;
import android.os.Build;
import android.os.Bundle;
import android.util.SizeF;
import android.widget.RemoteViews;
import java.util.HashMap;
import java.util.Map;

/** Now Playing: one resizable widget, a 2x2 layout that grows into a 4x2 one with previous and next. */
public class NowPlayingWidgetProvider extends AppWidgetProvider {

    /** Width from which the wide layout fits, in dp. */
    private static final float WIDE_FROM_DP = 250f;

    @Override
    public void onUpdate(Context context, AppWidgetManager manager, int[] ids) {
        render(context, manager, ids);
    }

    @Override
    public void onAppWidgetOptionsChanged(Context context, AppWidgetManager manager, int id, Bundle options) {
        render(context, manager, new int[] { id });
    }

    static void renderAll(Context context) {
        AppWidgetManager manager = AppWidgetManager.getInstance(context);
        int[] ids = manager.getAppWidgetIds(new ComponentName(context, NowPlayingWidgetProvider.class));
        if (ids.length > 0) render(context, manager, ids);
    }

    private static void render(Context context, AppWidgetManager manager, int[] ids) {
        WidgetPublisher publisher = WidgetPublisher.get(context);
        // A launcher can ask for a redraw in a process nothing is playing in.
        publisher.reconcile();
        WidgetSnapshot.NowPlaying now = publisher.store().nowPlaying();
        for (int id : ids) {
            RemoteViews views;
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                Map<SizeF, RemoteViews> sizes = new HashMap<>();
                sizes.put(new SizeF(110f, 110f), WidgetViews.nowPlaying(context, publisher.store(), now, false));
                sizes.put(new SizeF(WIDE_FROM_DP, 110f), WidgetViews.nowPlaying(context, publisher.store(), now, true));
                views = new RemoteViews(sizes);
            } else {
                int width = manager.getAppWidgetOptions(id).getInt(AppWidgetManager.OPTION_APPWIDGET_MIN_WIDTH, 0);
                views = WidgetViews.nowPlaying(context, publisher.store(), now, width >= WIDE_FROM_DP);
            }
            manager.updateAppWidget(id, views);
        }
    }
}
