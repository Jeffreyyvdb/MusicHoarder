package com.musichoarder.nativeaudio;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import androidx.annotation.Nullable;
import java.util.Arrays;
import java.util.List;

/**
 * A press on a Now Playing button while the widget showed a live player. Not exported: only the
 * PendingIntents this app handed the launcher reach it. The command goes through the same path as
 * a notification press (the page first, the device after a moment). When there is no player to
 * give it to, the widget stops promising one, and its buttons open the app on the next press.
 */
public class WidgetCommandReceiver extends BroadcastReceiver {

    private static final List<String> COMMANDS = Arrays.asList("play", "pause", "next", "previous");

    @Override
    public void onReceive(Context context, Intent intent) {
        if (!WidgetViews.ACTION_COMMAND.equals(intent.getAction())) return;
        String command = intent.getStringExtra(WidgetViews.EXTRA_COMMAND);
        if (command == null || !COMMANDS.contains(command)) return;
        handle(context, command, PlaybackService.engine());
    }

    static void handle(Context context, String command, @Nullable AudioEngine engine) {
        WidgetPublisher publisher = WidgetPublisher.get(context);
        if (engine == null || !engine.widgetCommand(command)) {
            publisher.demote();
            publisher.renderAll();
        }
    }
}
