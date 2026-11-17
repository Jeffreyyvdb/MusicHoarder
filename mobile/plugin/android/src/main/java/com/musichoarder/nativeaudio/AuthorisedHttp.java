package com.musichoarder.nativeaudio;

import android.webkit.CookieManager;
import okhttp3.Interceptor;
import okhttp3.OkHttpClient;
import okhttp3.Request;

/**
 * The one HTTP client that carries the web view's session. Streams and covers are authorised by
 * that cookie, and a network interceptor runs on every hop, so a redirect to another host is
 * sent that host's cookies (none), never the session. The player, the media session's bitmap
 * loader and the widgets' cover downloads all go through it.
 */
final class AuthorisedHttp {

    private static final OkHttpClient CLIENT = build();

    private AuthorisedHttp() {}

    static OkHttpClient client() {
        return CLIENT;
    }

    private static OkHttpClient build() {
        Interceptor cookies = chain -> {
            Request request = chain.request();
            String cookie = CookieManager.getInstance().getCookie(request.url().toString());
            Request.Builder builder = request.newBuilder().removeHeader("Cookie");
            if (cookie != null && !cookie.isEmpty()) builder.header("Cookie", cookie);
            return chain.proceed(builder.build());
        };
        return new OkHttpClient.Builder().addNetworkInterceptor(cookies).build();
    }
}
