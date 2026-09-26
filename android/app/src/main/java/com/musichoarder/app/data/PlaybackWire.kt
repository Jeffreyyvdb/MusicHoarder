package com.musichoarder.app.data

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/*
 * The playback sync ("Connect") wire contract: `GET /api/playback`, the `/api/playback/stream`
 * events, `POST /api/playback/state` and `POST /api/playback/command`. Three codebases speak it —
 * the API, the web app and this one — so every field name here is the API's camelCase spelling,
 * and every default only exists so an older or newer server that leaves a field out does not fail
 * the whole decode.
 */

/** One player instance of the account: a browser tab, or this app on one phone. */
@Serializable
data class PlaybackDevice(
    val deviceId: String,
    /** Web: one per browser profile, so another tab of the same browser can say "Another tab". */
    val installId: String? = null,
    val name: String = "",
    /** [DeviceKind]: drives the icon only. */
    val kind: String = DeviceKind.UNKNOWN,
    /** "web" | "android". */
    val client: String = "",
    val online: Boolean = false,
    val isActive: Boolean = false,
)

/**
 * The account's one playback session, as the server last knew it.
 *
 * [positionMs] is the position as of the moment the server produced the message — it has already
 * extrapolated while playing — so a client only carries it forward from its own receipt time
 * ([extrapolatedPositionMs]). [live] is false once the device holding the session is no longer
 * reachable: the remembered session every device shows as "Last played on …".
 */
@Serializable
data class PlaybackSessionDto(
    val version: Long,
    val songId: Int,
    val title: String? = null,
    val artist: String? = null,
    val album: String? = null,
    val queue: List<Int> = emptyList(),
    val queueIndex: Int = 0,
    val positionMs: Long = 0,
    val durationMs: Long? = null,
    val isPlaying: Boolean = false,
    val playbackRate: Double = 1.0,
    val radioSeedId: Int? = null,
    val shuffle: Boolean = false,
    val activeDeviceId: String? = null,
    val activeDeviceName: String? = null,
    val live: Boolean = false,
    /** The most recent command the active device acknowledged — how a sender knows it landed. */
    val lastCommandId: String? = null,
    val updatedAtUtc: String? = null,
)

/** `GET /api/playback`, and the stream's `snapshot` event: everything, replacing what we knew. */
@Serializable
data class PlaybackSnapshot(
    val session: PlaybackSessionDto? = null,
    val devices: List<PlaybackDevice> = emptyList(),
)

/** The stream's `session` event. */
@Serializable
data class PlaybackSessionEvent(val session: PlaybackSessionDto? = null)

/** The stream's `devices` event. */
@Serializable
data class PlaybackDevicesEvent(val devices: List<PlaybackDevice> = emptyList())

/** The stream's `command` event — only ever sent to the target device's own streams. */
@Serializable
data class PlaybackCommandEvent(
    val commandId: String,
    /** [PlaybackCommand]. */
    val command: String,
    val positionMs: Long? = null,
    val fromDeviceId: String? = null,
    val fromDeviceName: String? = null,
)

/**
 * `POST /api/playback/state` — this device's report.
 *
 * No defaults on purpose: the report is encoded with [PlaybackJson], which writes every field,
 * nulls included, because `queue: null` is itself a statement ("unchanged") rather than an
 * omission.
 */
@Serializable
data class PlaybackStateReport(
    val deviceId: String,
    val installId: String?,
    val deviceName: String,
    val deviceKind: String,
    val client: String,
    /** A local play intent just happened here (or this device executed a transfer/resume). */
    val claim: Boolean,
    val inResponseTo: String?,
    val songId: Int,
    val title: String?,
    val artist: String?,
    val album: String?,
    /** Required on a claim; null otherwise means "the queue has not changed". */
    val queue: List<Int>?,
    val queueIndex: Int,
    val positionMs: Long,
    val durationMs: Long?,
    val isPlaying: Boolean,
    val playbackRate: Double,
    val radioSeedId: Int?,
    val shuffle: Boolean,
)

/** `accepted: false` means another device holds the session: stop playing here. */
@Serializable
data class PlaybackStateResponse(
    val accepted: Boolean = false,
    val session: PlaybackSessionDto? = null,
)

/** `POST /api/playback/command`. [targetDeviceId] is required only for a transfer. */
@Serializable
data class PlaybackCommandBody(
    val fromDeviceId: String,
    val targetDeviceId: String?,
    val command: String,
    val positionMs: Long?,
)

/** `202 { commandId }`. */
@Serializable
data class PlaybackCommandAccepted(val commandId: String)

/** `409 { error: "device_offline" | "not_active_device" }`, `404 { error: "no_session" }`. */
@Serializable
data class PlaybackErrorBody(val error: String? = null)

/** The command names, exactly as the wire spells them. */
object PlaybackCommand {
    const val PAUSE = "pause"
    const val RESUME = "resume"
    const val NEXT = "next"
    const val PREVIOUS = "previous"
    const val SEEK = "seek"
    const val TRANSFER = "transfer"
}

/** The stream's event types. */
object PlaybackEvent {
    const val SNAPSHOT = "snapshot"
    const val SESSION = "session"
    const val DEVICES = "devices"
    const val COMMAND = "command"
    const val PING = "ping"
}

/** `PlaybackDevice.kind` values. */
object DeviceKind {
    const val COMPUTER = "computer"
    const val PHONE = "phone"
    const val TABLET = "tablet"
    const val UNKNOWN = "unknown"
}

/** This client's name on the wire. */
const val PLAYBACK_CLIENT_ANDROID = "android"

/**
 * The one decoder/encoder for this contract. Unknown keys are ignored (the server may grow the
 * payload), and defaults are encoded so a report always carries every field — see
 * [PlaybackStateReport].
 */
val PlaybackJson = Json {
    ignoreUnknownKeys = true
    isLenient = true
    encodeDefaults = true
    explicitNulls = true
}
