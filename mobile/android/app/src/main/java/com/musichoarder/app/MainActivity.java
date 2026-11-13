package com.musichoarder.app;

import android.content.Intent;
import android.net.Uri;
import android.os.Bundle;
import com.getcapacitor.BridgeActivity;
import com.getcapacitor.CapConfig;
import com.getcapacitor.Logger;
import com.musichoarder.nativeaudio.ShellServerPlugin;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import org.json.JSONObject;

public class MainActivity extends BridgeActivity {

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        config = configForChosenServer();
        super.onCreate(savedInstanceState);
    }

    /**
     * The bundled config, pointed at the server the user chose on the bundled page (stored by
     * `ShellServerPlugin`, which recreates this activity when it changes). Without a choice it
     * is null, and the bridge loads the bundled config as it is: the build's own `server.url`, or
     * none, and then the bundled page, which asks for one.
     */
    private CapConfig configForChosenServer() {
        String chosen = ShellServerPlugin.preferences(this).getString(ShellServerPlugin.KEY, null);
        if (chosen == null || !chosen.startsWith("https://")) return null;
        try (InputStream in = getAssets().open("capacitor.config.json")) {
            ByteArrayOutputStream bytes = new ByteArrayOutputStream();
            byte[] buffer = new byte[8192];
            int read;
            while ((read = in.read(buffer)) != -1) bytes.write(buffer, 0, read);
            JSONObject json = new JSONObject(bytes.toString(StandardCharsets.UTF_8.name()));
            JSONObject server = json.optJSONObject("server");
            if (server == null) {
                server = new JSONObject();
                json.put("server", server);
            }
            server.put("url", chosen);
            // The signed-in home, as for a build's own server (capacitor.config.ts).
            server.put("appStartPath", "/overview");
            File dir = new File(getFilesDir(), "shell-config");
            if (!dir.isDirectory() && !dir.mkdirs()) return null;
            try (FileOutputStream out = new FileOutputStream(new File(dir, "capacitor.config.json"))) {
                out.write(json.toString().getBytes(StandardCharsets.UTF_8));
            }
            return CapConfig.loadFromFile(this, dir.getAbsolutePath());
        } catch (Exception e) {
            Logger.error("Could not point the app at the chosen server", e);
            return null;
        }
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        openSignInLink(intent);
    }

    /**
     * `musichoarder://auth?token=…&url=…`, the link on the page an emailed sign-in link opens in
     * the browser when it was asked for from this app (the frontend's `/auth/callback`). The web
     * view has its own cookies, so the token is spent there: `/auth/app`, loaded in the web view,
     * passes it to the callback, which sets the session cookie where the app can use it. That
     * page does so only when a link was asked for from this app, since anything on the phone can
     * open a `musichoarder://` link with a token of its own. BridgeActivity passes the launch
     * intent through `onNewIntent` too, so a cold start takes the same path.
     */
    private void openSignInLink(Intent intent) {
        Uri link = intent == null ? null : intent.getData();
        if (link == null || bridge == null) return;
        if (!"musichoarder".equalsIgnoreCase(link.getScheme()) || !"auth".equalsIgnoreCase(link.getHost())) return;
        String token = link.getQueryParameter("token");
        String configured = bridge.getServerUrl();
        if (token == null || token.isEmpty() || configured == null) return;
        Uri server = Uri.parse(configured);
        if (server.getScheme() == null || !server.getScheme().toLowerCase().startsWith("http")) return;

        // The link names the server it was made for. The app only ever loads its own, so a link
        // made for another one, or naming none, goes nowhere.
        String named = link.getQueryParameter("url");
        if (named == null || named.isEmpty() || !sameOrigin(Uri.parse(named), server)) return;

        Uri callback = new Uri.Builder()
            .scheme(server.getScheme())
            .encodedAuthority(server.getEncodedAuthority())
            .path("/auth/app")
            .appendQueryParameter("token", token)
            .build();
        bridge.getWebView().loadUrl(callback.toString());
        intent.setData(null); // spent: a recreated activity must not replay it
    }

    private static boolean sameOrigin(Uri left, Uri right) {
        return (
            String.valueOf(left.getScheme()).equalsIgnoreCase(String.valueOf(right.getScheme())) &&
            String.valueOf(left.getHost()).equalsIgnoreCase(String.valueOf(right.getHost())) &&
            left.getPort() == right.getPort()
        );
    }
}
