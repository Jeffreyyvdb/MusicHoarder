package com.musichoarder.app.player

import com.musichoarder.app.data.DeviceKind
import com.musichoarder.app.data.PlaybackMode
import com.musichoarder.app.data.PlaybackSessionDto
import com.musichoarder.app.data.Track
import com.musichoarder.app.data.deviceLineFor

/**
 * The player UI's state for a session held by another device (or only remembered), so the mini
 * player and Now Playing render it exactly as they render the local player — same fields, same
 * composables — with [PlayerUiState.mode] telling the transport where its taps go.
 *
 * The song is resolved against this phone's library ([track]) for the cover and the credits the
 * rest of the app shows, and falls back to the session's display hints for a song the library does
 * not have ("Unknown title" / "Unknown artist" when even those are blank, as the web's `hintSong`
 * says). [positionMs] is already carried forward to now. A remembered session reads as paused
 * whatever it last said: nothing is playing it.
 *
 * [heldHere] is the session this very device held before its process was restarted: it needs no
 * "Last played on" line naming the phone in your hand (the web shows none in that case either).
 * [deviceKind] is the holding device's, for the icon beside the line.
 */
fun sessionPlayerState(
    session: PlaybackSessionDto,
    mode: PlaybackMode,
    positionMs: Long,
    track: Track?,
    artworkUrl: String?,
    heldHere: Boolean = false,
    deviceKind: String = DeviceKind.UNKNOWN,
    devicesAvailable: Boolean = true,
): PlayerUiState {
    val playing = mode == PlaybackMode.Remote && session.isPlaying
    return PlayerUiState(
        trackId = session.songId,
        title = track?.title ?: session.title?.trim()?.takeIf(String::isNotEmpty) ?: "Unknown title",
        artist = track?.artist ?: session.artist?.trim()?.takeIf(String::isNotEmpty) ?: "Unknown artist",
        album = track?.album ?: session.album.orEmpty(),
        hasCover = artworkUrl != null,
        artworkUrl = artworkUrl,
        isPlaying = playing,
        isBuffering = false,
        positionMs = positionMs,
        durationMs = session.durationMs?.takeIf { it > 0 } ?: track?.durationMs ?: 0,
        // The other device owns the station, so a seed means there is always a next.
        hasNext = session.queueIndex < session.queue.lastIndex || session.radioSeedId != null,
        hasPrevious = session.queueIndex > 0,
        shuffleEnabled = session.shuffle,
        playbackRate = session.playbackRate.toFloat(),
        mode = mode,
        deviceLine = if (heldHere) null else deviceLineFor(mode, session.activeDeviceName, playing),
        deviceKind = deviceKind,
        devicesAvailable = devicesAvailable,
    )
}
