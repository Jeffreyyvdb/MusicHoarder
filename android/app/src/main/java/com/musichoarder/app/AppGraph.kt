package com.musichoarder.app

import android.app.Application
import android.content.Context
import coil3.ImageLoader
import coil3.PlatformContext
import coil3.SingletonImageLoader
import coil3.network.okhttp.OkHttpNetworkFetcherFactory
import coil3.request.crossfade
import com.musichoarder.app.data.AuthInterceptor
import com.musichoarder.app.data.LibraryRepository
import com.musichoarder.app.data.MusicHoarderApi
import com.musichoarder.app.data.SessionStore
import okhttp3.OkHttpClient
import java.util.concurrent.TimeUnit

/**
 * Hand-rolled dependency graph. The app has one HTTP client, one API, one library cache — a DI
 * framework would be more moving parts than wiring.
 *
 * The single [OkHttpClient] is shared by the API calls, the ExoPlayer data source, and Coil, so all
 * three reuse connections and all three carry the bearer token.
 */
class AppGraph(context: Context) {
    val sessions = SessionStore(context)

    val httpClient: OkHttpClient = OkHttpClient.Builder()
        .addNetworkInterceptor(AuthInterceptor(sessions))
        .connectTimeout(15, TimeUnit.SECONDS)
        // The library dump is a big response from a server that may be doing pipeline work.
        .readTimeout(60, TimeUnit.SECONDS)
        .build()

    val api = MusicHoarderApi(httpClient, sessions)

    val library = LibraryRepository(api)

    val imageLoader: ImageLoader = ImageLoader.Builder(context)
        .components { add(OkHttpNetworkFetcherFactory(callFactory = { httpClient })) }
        .crossfade(true)
        .build()
}

class MusicHoarderApp : Application(), SingletonImageLoader.Factory {
    lateinit var graph: AppGraph
        private set

    override fun onCreate() {
        super.onCreate()
        graph = AppGraph(this)
    }

    /** Cover art is behind the same bearer token as everything else, so Coil uses our client. */
    override fun newImageLoader(context: PlatformContext): ImageLoader = graph.imageLoader
}
