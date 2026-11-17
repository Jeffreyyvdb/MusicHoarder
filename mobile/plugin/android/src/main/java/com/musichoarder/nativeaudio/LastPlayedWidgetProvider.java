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
import java.util.List;
import java.util.Map;

/** Last played: up to four covers of the Overview's shelf, with titles once there is height for them. */
public class LastPlayedWidgetProvider extends AppWidgetProvider {

    /** Height from which the titles fit under the covers, in dp. */
    private static final float TALL_FROM_DP = 110f;

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
        int[] ids = manager.getAppWidgetIds(new ComponentName(context, LastPlayedWidgetProvider.class));
        if (ids.length > 0) render(context, manager, ids);
    }

    private static void render(Context context, AppWidgetManager manager, int[] ids) {
        WidgetStore store = WidgetPublisher.get(context).store();
        List<WidgetSnapshot.ShelfItem> shelf = store.shelf();
        for (int id : ids) {
            RemoteViews views;
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                Map<SizeF, RemoteViews> sizes = new HashMap<>();
                sizes.put(new SizeF(250f, 70f), WidgetViews.lastPlayed(context, store, shelf, false));
                sizes.put(new SizeF(250f, TALL_FROM_DP), WidgetViews.lastPlayed(context, store, shelf, true));
                views = new RemoteViews(sizes);
            } else {
                int height = manager.getAppWidgetOptions(id).getInt(AppWidgetManager.OPTION_APPWIDGET_MIN_HEIGHT, 0);
                views = WidgetViews.lastPlayed(context, store, shelf, height >= TALL_FROM_DP);
            }
            manager.updateAppWidget(id, views);
        }
    }
}
