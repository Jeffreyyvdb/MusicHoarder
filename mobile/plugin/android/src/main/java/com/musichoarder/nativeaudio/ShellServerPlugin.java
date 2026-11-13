package com.musichoarder.nativeaudio;

import android.app.Activity;
import android.content.Context;
import android.content.SharedPreferences;
import android.net.Uri;
import com.getcapacitor.JSObject;
import com.getcapacitor.Plugin;
import com.getcapacitor.PluginCall;
import com.getcapacitor.PluginMethod;
import com.getcapacitor.annotation.CapacitorPlugin;
import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.Locale;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import org.json.JSONObject;

/**
 * Which MusicHoarder server the app loads. The app ships without one (a build may still name a
 * default, `MH_SERVER_URL`), so the bundled page asks for an address and hands it here. The app
 * itself reads the choice when it builds its bridge (`MainActivity.java`), under the same
 * preferences file and key: change both together.
 *
 * The iOS twin is `ShellServerPlugin.swift`; the web side is `mobile/www/index.html` and
 * `frontend/src/lib/native/shell.ts`.
 */
@CapacitorPlugin(name = "ShellServer")
public class ShellServerPlugin extends Plugin {

    /** Read by MainActivity. */
    public static final String PREFERENCES = "musichoarder_shell";
    public static final String KEY = "server_url";

    private final ExecutorService network = Executors.newSingleThreadExecutor();

    /** The server the app has loaded, or null when it is showing the bundled page with none. */
    @PluginMethod
    public void getServer(PluginCall call) {
        String loaded = getBridge().getServerUrl();
        Uri origin = loaded == null ? null : origin(Uri.parse(loaded));
        JSObject result = new JSObject();
        result.put("url", origin == null ? JSONObject.NULL : origin.toString());
        result.put("chosen", preferences(getContext()).getString(KEY, null) != null);
        call.resolve(result);
    }

    /**
     * Take `url` as the server, if it is a MusicHoarder server reachable over https, and load it.
     * Rejected with a code the page words: `invalid`, `unreachable` or `not_musichoarder`.
     */
    @PluginMethod
    public void setServer(PluginCall call) {
        Uri origin = parse(call.getString("url"));
        if (origin == null) {
            call.reject("That is not a web address.", "invalid");
            return;
        }
        network.execute(() -> {
            String failure = verify(origin);
            if (failure != null) {
                call.reject(
                    failure.equals("unreachable") ? "Could not reach that address." : "That address is not a MusicHoarder server.",
                    failure
                );
                return;
            }
            preferences(getContext()).edit().putString(KEY, origin.toString()).commit();
            JSObject result = new JSObject();
            result.put("url", origin.toString());
            call.resolve(result);
            reload();
        });
    }

    /** Forget the choice: the app loads the build's own server again, or asks for one. */
    @PluginMethod
    public void clearServer(PluginCall call) {
        preferences(getContext()).edit().remove(KEY).commit();
        call.resolve();
        reload();
    }

    @Override
    protected void handleOnDestroy() {
        network.shutdownNow();
    }

    /** Build the activity again, which builds the bridge for the new choice. */
    private void reload() {
        // Through the web view: getActivity() is typed as AppCompat's, which this module does not
        // depend on.
        Context context = getBridge().getWebView().getContext();
        if (!(context instanceof Activity)) return;
        Activity activity = (Activity) context;
        // After the reply has had a moment to reach the page that asked. The music stops first, as
        // on iOS: it belongs to the server being left, and the service would otherwise play on
        // under a page that knows nothing about it.
        activity.getWindow().getDecorView().postDelayed(() -> {
            AudioEngine engine = PlaybackService.engine();
            if (engine != null) engine.stop();
            activity.recreate();
        }, 100);
    }

    public static SharedPreferences preferences(Context context) {
        return context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE);
    }

    /** "music.example.com", "https://music.example.com/overview" → "https://music.example.com". */
    static Uri parse(String raw) {
        if (raw == null) return null;
        String text = raw.trim();
        if (text.isEmpty()) return null;
        if (!text.contains("://")) text = "https://" + text;
        Uri uri = Uri.parse(text);
        if (!"https".equalsIgnoreCase(uri.getScheme())) return null;
        return origin(uri);
    }

    static Uri origin(Uri uri) {
        String scheme = uri.getScheme() == null ? "" : uri.getScheme().toLowerCase(Locale.ROOT);
        String host = uri.getHost();
        if (!(scheme.equals("https") || scheme.equals("http")) || host == null || host.isEmpty()) return null;
        String authority = host.toLowerCase(Locale.ROOT) + (uri.getPort() > 0 ? ":" + uri.getPort() : "");
        return new Uri.Builder().scheme(scheme).encodedAuthority(authority).build();
    }

    /**
     * Null when the server's web app manifest names MusicHoarder; else why not. Anything else (a
     * typo that happens to load, a captive portal) would leave the app on a page with no way back
     * to the one that asks.
     */
    private static String verify(Uri origin) {
        HttpURLConnection connection = null;
        try {
            connection = (HttpURLConnection) new URL(origin + "/manifest.webmanifest").openConnection();
            connection.setConnectTimeout(15000);
            connection.setReadTimeout(15000);
            connection.setUseCaches(false);
            int status = connection.getResponseCode();
            Uri landed = origin(Uri.parse(connection.getURL().toString()));
            if (status < 200 || status >= 300 || landed == null || !landed.equals(origin)) return "not_musichoarder";
            try (InputStream in = connection.getInputStream()) {
                ByteArrayOutputStream body = new ByteArrayOutputStream();
                byte[] buffer = new byte[8192];
                int read;
                while ((read = in.read(buffer)) != -1 && body.size() < 256 * 1024) body.write(buffer, 0, read);
                JSONObject manifest = new JSONObject(body.toString(StandardCharsets.UTF_8.name()));
                return "MusicHoarder".equals(manifest.optString("name")) ? null : "not_musichoarder";
            }
        } catch (java.io.IOException e) {
            return "unreachable";
        } catch (Exception e) {
            return "not_musichoarder";
        } finally {
            if (connection != null) connection.disconnect();
        }
    }
}
