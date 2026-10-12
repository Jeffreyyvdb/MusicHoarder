package com.musichoarder.app;

import android.content.Intent;
import android.net.Uri;
import com.getcapacitor.BridgeActivity;

public class MainActivity extends BridgeActivity {

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        openSignInLink(intent);
    }

    /**
     * `musichoarder://auth?token=…&url=…`, the link on the page an emailed sign-in link opens in
     * the browser when it was asked for from this app (the frontend's `/auth/callback`). The web
     * view has its own cookies, so the token is spent here: the same callback, loaded in the web
     * view, sets the session cookie where the app can use it. BridgeActivity passes the launch
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
        // made for another one (or made up) goes nowhere.
        String named = link.getQueryParameter("url");
        if (named != null && !named.isEmpty() && !sameOrigin(Uri.parse(named), server)) return;

        Uri callback = new Uri.Builder()
            .scheme(server.getScheme())
            .encodedAuthority(server.getEncodedAuthority())
            .path("/auth/callback")
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
