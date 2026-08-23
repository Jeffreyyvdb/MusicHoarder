package com.musichoarder.app.player

import android.content.Context
import androidx.annotation.OptIn
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.DataSource
import androidx.media3.datasource.DefaultDataSource
import androidx.media3.datasource.okhttp.OkHttpDataSource
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.exoplayer.source.MediaSource
import androidx.media3.extractor.DefaultExtractorsFactory
import okhttp3.OkHttpClient

/**
 * How both players — the song and the muted clip behind it — reach the server.
 *
 * Every MusicHoarder endpoint is behind the bearer token, so media goes through the app's
 * authenticated OkHttp client rather than Media3's default HTTP stack.
 */
@OptIn(UnstableApi::class)
internal object MediaSources {

    fun dataSourceFactory(context: Context, httpClient: OkHttpClient): DataSource.Factory =
        DefaultDataSource.Factory(context, OkHttpDataSource.Factory(httpClient))

    fun mediaSourceFactory(dataSourceFactory: DataSource.Factory): MediaSource.Factory =
        DefaultMediaSourceFactory(dataSourceFactory, extractorsFactory())

    /**
     * Extractors that will seek in a plain MP3 stream.
     *
     * This is what made the scrubber and the lyric lines inert. A constant-bitrate MP3 carries no
     * seek table unless its encoder wrote a Xing/VBRI header, and plenty in a hoarded library did
     * not; the frontend's `/api/mh` proxy also drops `Content-Length`, so ExoPlayer cannot fall back
     * to deriving one from the file size either. With no seek map the current item reports itself
     * unseekable, which drops `COMMAND_SEEK_IN_CURRENT_MEDIA_ITEM` from the session's available
     * commands — and [androidx.media3.session.MediaController.seekTo] *silently returns* when a
     * command is unavailable. Every seek in the app went to that `return`: no error, no log, the
     * position simply never moved.
     *
     * Constant-bitrate seeking estimates the byte offset from the first frame's bitrate instead.
     * That is exactly what a browser's `<audio>` element does with the same file, which is why the
     * web player has always been able to seek these tracks. `...AlwaysEnabled` extends it to streams
     * whose length is unknown — the case the proxy creates. Neither flag has any effect on a
     * container that carries real seek information (FLAC, M4A, a Xing-tagged MP3): those keep using
     * their own tables and stay sample-accurate.
     */
    private fun extractorsFactory() = DefaultExtractorsFactory()
        .setConstantBitrateSeekingEnabled(true)
        .setConstantBitrateSeekingAlwaysEnabled(true)
}
