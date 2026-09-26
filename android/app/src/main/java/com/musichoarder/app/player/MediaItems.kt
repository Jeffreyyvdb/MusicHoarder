package com.musichoarder.app.player

import android.net.Uri
import android.os.Bundle
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.Player
import com.musichoarder.app.data.MusicHoarderApi
import com.musichoarder.app.data.Track

/*
 * What the queue is made of, and the two facts it has to carry for whoever reads the player later —
 * the app's own controller or the playback sync inside the service, which outlives the UI:
 *
 * - whether an item came from a share link ([isShareItem]): a share's ids belong to the sharing
 *   server, so they are never reported as the account's session;
 * - the station seed ([stationSeedId]): the track the radio is built from, which the session
 *   reports and another device adopts. It lives in the player's playlist metadata rather than in
 *   the controller, so it survives the Activity and reaches the service without a second channel.
 */

/** Builds the queue item for [track]; the same item whether a tap or an adopted session plays it. */
fun Track.toMediaItem(api: MusicHoarderApi): MediaItem = MediaItem.Builder()
    .setMediaId(id.toString())
    // A share track carries its own absolute token-in-path URL; only library tracks go through the
    // paired route (which throws when unpaired — shares must not).
    .setUri(streamUrl ?: api.streamUrl(id))
    .setMediaMetadata(
        MediaMetadata.Builder()
            .setTitle(title)
            .setArtist(artist)
            .setAlbumTitle(album)
            .setAlbumArtist(albumArtist)
            // The library already knows how long the track is. ExoPlayer only learns it once it has
            // parsed enough of the stream, which over the internet can take most of a minute — and
            // until then the bar is inert and the label reads "--:--". This is the web transport's
            // `fallbackDuration` prop, carried on the item.
            .setDurationMs(durationMs)
            .setArtworkUri(
                // 640 is the largest server-side thumbnail bucket — enough for the lock screen.
                artworkUrl?.let(Uri::parse)
                    ?: if (hasCover) Uri.parse(api.coverUrl(id, 640)) else null
            )
            .setIsBrowsable(false)
            .setIsPlayable(true)
            .apply { if (streamUrl != null) setExtras(shareItemExtras()) }
            .build()
    )
    .build()

/** The item extras that mark a share link's track ([isShareItem]). */
internal fun shareItemExtras(): Bundle = Bundle().apply { putBoolean(EXTRA_SHARE_ITEM, true) }

/** True when the loaded item came from a share link rather than the paired library. */
val Player.isShareItem: Boolean
    get() = currentMediaItem?.mediaMetadata?.extras?.getBoolean(EXTRA_SHARE_ITEM, false) == true

/**
 * The playlist metadata that names the station's seed; [MediaMetadata.EMPTY] for none.
 *
 * The seed is read back from the extras, but it is written into the description as well, because
 * `MediaMetadata.equals` only asks whether two extras bundles are both null, never what they hold.
 * Seeds carried in the extras alone therefore compare equal, and both ExoPlayer and a
 * MediaController drop a "change" to an equal value — the first station would never let go.
 */
fun stationMetadata(seedId: Int?): MediaMetadata =
    if (seedId == null) {
        MediaMetadata.EMPTY
    } else {
        MediaMetadata.Builder()
            .setDescription("$STATION_DESCRIPTION_PREFIX$seedId")
            .setExtras(Bundle().apply { putInt(EXTRA_STATION_SEED, seedId) })
            .build()
    }

/** The station's seed, or null when the queue has none (a share queue, or nothing loaded). */
val Player.stationSeedId: Int?
    get() = playlistMetadata.extras
        ?.takeIf { it.containsKey(EXTRA_STATION_SEED) }
        ?.getInt(EXTRA_STATION_SEED)

private const val EXTRA_SHARE_ITEM = "com.musichoarder.app.SHARE_ITEM"
private const val EXTRA_STATION_SEED = "com.musichoarder.app.STATION_SEED"
private const val STATION_DESCRIPTION_PREFIX = "station:"
