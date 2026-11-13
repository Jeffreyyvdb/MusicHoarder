package com.musichoarder.nativeaudio;

import android.net.Uri;
import androidx.annotation.Nullable;
import androidx.media3.common.MediaItem;
import androidx.media3.common.MediaMetadata;

/** One song as the web player describes it. */
final class Track {

    final String url;
    final String title;
    final String artist;
    final String album;

    @Nullable
    final String artwork;

    Track(String url, String title, String artist, String album, @Nullable String artwork) {
        this.url = url;
        this.title = title;
        this.artist = artist;
        this.album = album;
        this.artwork = artwork;
    }

    /** The URL doubles as the media id: it is how the page and the player name the same track. */
    MediaItem toMediaItem() {
        MediaMetadata metadata = new MediaMetadata.Builder()
            .setTitle(title)
            .setArtist(artist)
            .setAlbumTitle(album)
            .setArtworkUri(artwork == null ? null : Uri.parse(artwork))
            .build();
        return new MediaItem.Builder().setMediaId(url).setUri(url).setMediaMetadata(metadata).build();
    }
}
