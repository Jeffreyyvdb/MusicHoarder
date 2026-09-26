package com.musichoarder.app.data

import android.content.res.Configuration
import kotlin.math.max
import kotlin.math.min
import kotlin.math.roundToLong

/*
 * The rules of playback sync ("Connect"), kept pure so they are pinned by plain JUnit tests and can
 * be compared case for case with the web client's TypeScript twin. Everything here is a decision
 * both clients have to make identically — which mode the player is in, where a remote song is by
 * now, how a queue is cut to the wire's cap — and nothing here touches a player or the network;
 * `player/PlaybackConnect.kt` does that.
 */

/**
 * What the player UI is showing.
 *
 * - [Local]: this phone's own player — no session, this device holds it, or the feature is off.
 * - [Remote]: another *reachable* device holds the session; the transport controls that device.
 * - [Remembered]: nobody reachable holds it; the last session is shown paused, and Play resumes it
 *   here.
 */
enum class PlaybackMode { Local, Remote, Remembered }

/** The spec's rule, verbatim: nothing Android-specific lives in here (see [displayModeFor]). */
fun playbackModeFor(session: PlaybackSessionDto?, myDeviceId: String, featureOn: Boolean): PlaybackMode =
    when {
        !featureOn || session == null -> PlaybackMode.Local
        // Even while this device's own stream is momentarily down: it is still the one playing.
        session.activeDeviceId == myDeviceId -> PlaybackMode.Local
        session.live -> PlaybackMode.Remote
        else -> PlaybackMode.Remembered
    }

/**
 * [playbackModeFor], plus the two things only this client has to consider.
 *
 * - A claim still on its way shows the local player at once. The tap that started it is the
 *   truth; waiting for the server to agree would flash the other device's song for a round trip.
 * - The session naming *this* device while the player holds nothing means the process was killed
 *   since (the device id outlives it, which a browser tab's does not). Shown as remembered, so a
 *   cold start offers "continue what you were playing" instead of an empty player.
 */
fun displayModeFor(
    mode: PlaybackMode,
    session: PlaybackSessionDto?,
    myDeviceId: String,
    localLoaded: Boolean,
    claimInFlight: Boolean,
): PlaybackMode = when {
    claimInFlight -> PlaybackMode.Local
    mode == PlaybackMode.Local && session != null && session.activeDeviceId == myDeviceId && !localLoaded ->
        PlaybackMode.Remembered
    else -> mode
}

/**
 * Where a session's song is by now: the reported position carried forward by the time since it was
 * received, at the session's rate, while it is playing — never below zero and never past the end.
 *
 * [elapsedMs] must come from a monotonic clock (`SystemClock.elapsedRealtime`); a wall clock can
 * step backwards. A rate that is not a positive number is read as 1×.
 */
fun extrapolatedPositionMs(
    positionMs: Long,
    durationMs: Long?,
    isPlaying: Boolean,
    playbackRate: Double,
    elapsedMs: Long,
): Long {
    val rate = if (playbackRate.isFinite() && playbackRate > 0.0) playbackRate else 1.0
    val advanced = if (isPlaying) positionMs + (elapsedMs.coerceAtLeast(0) * rate).roundToLong() else positionMs
    val floored = advanced.coerceAtLeast(0)
    return if (durationMs != null && durationMs > 0) floored.coerceAtMost(durationMs) else floored
}

/** At most this many ids travel in a session, either way. */
const val PLAYBACK_QUEUE_CAP = 1000

/** How much of what was already played a trimmed queue keeps before the current song. */
const val PLAYBACK_QUEUE_LOOK_BEHIND = 50

data class TrimmedQueue(val queue: List<Int>, val queueIndex: Int)

/**
 * The wire's queue cap. A queue within [PLAYBACK_QUEUE_CAP] goes as it is; a longer one keeps the
 * current song, starts at most [PLAYBACK_QUEUE_LOOK_BEHIND] ids before it, takes up to the cap from
 * there, and re-bases the index. The server enforces the same rule (`PlaybackRules.TrimQueue`), and
 * since a trimmed queue is within the cap, what a client sent never moves under it.
 */
fun trimQueue(queue: List<Int>, queueIndex: Int): TrimmedQueue {
    if (queue.size <= PLAYBACK_QUEUE_CAP) return TrimmedQueue(queue.toList(), queueIndex)
    val start = max(0, queueIndex.coerceIn(queue.indices) - PLAYBACK_QUEUE_LOOK_BEHIND)
    val end = min(queue.size, start + PLAYBACK_QUEUE_CAP)
    return TrimmedQueue(queue.subList(start, end).toList(), queueIndex - start)
}

/**
 * The icon this device shows on other screens, from its configuration: 600dp and up is a tablet
 * (Material's medium window, the line the app's own navigation rail uses), anything smaller a
 * phone, and a television, car, watch or headset — none of which this app is built for — unknown.
 */
fun deviceKindFor(smallestScreenWidthDp: Int, uiModeType: Int): String = when (uiModeType) {
    Configuration.UI_MODE_TYPE_TELEVISION,
    Configuration.UI_MODE_TYPE_CAR,
    Configuration.UI_MODE_TYPE_WATCH,
    Configuration.UI_MODE_TYPE_APPLIANCE,
    Configuration.UI_MODE_TYPE_VR_HEADSET,
    -> DeviceKind.UNKNOWN

    else -> when {
        smallestScreenWidthDp >= 600 -> DeviceKind.TABLET
        // 0 is Configuration.SMALLEST_SCREEN_WIDTH_DP_UNDEFINED.
        smallestScreenWidthDp > 0 -> DeviceKind.PHONE
        else -> DeviceKind.UNKNOWN
    }
}

/** The wire's cap on a device name. */
const val PLAYBACK_DEVICE_NAME_CAP = 64

/**
 * The name other devices see: what the owner called the phone in Settings (`Settings.Global
 * .DEVICE_NAME` — "Alex's Pixel"), else the model ("Pixel 8"), cut to the wire's 64 characters.
 */
fun deviceNameFor(settingsName: String?, model: String?): String {
    val name = settingsName?.trim()?.takeIf(String::isNotEmpty)
        ?: model?.trim()?.takeIf(String::isNotEmpty)
        ?: "Android"
    return name.take(PLAYBACK_DEVICE_NAME_CAP)
}

private const val NO_VERSION = Long.MIN_VALUE

/**
 * What this device knows about the account's session and devices, and when it learned it.
 *
 * [receivedAtMs] is the monotonic clock at receipt: the session's position is "as of then".
 */
data class SyncKnowledge(
    val session: PlaybackSessionDto? = null,
    val devices: List<PlaybackDevice> = emptyList(),
    val receivedAtMs: Long = 0,
    /** The newest session version applied. A `session` event older than this is stale news. */
    val appliedVersion: Long = NO_VERSION,
) {
    /**
     * A snapshot always replaces what we knew — it is the first thing every (re)connection says,
     * and after an API restart the versions may have gone backwards, which a version check would
     * then refuse for good.
     */
    fun withSnapshot(snapshot: PlaybackSnapshot, nowMs: Long): SyncKnowledge = SyncKnowledge(
        session = snapshot.session,
        devices = snapshot.devices,
        receivedAtMs = nowMs,
        appliedVersion = snapshot.session?.version ?: NO_VERSION,
    )

    /** A session only moves knowledge forward: one no newer than what was applied is ignored. */
    fun withSession(session: PlaybackSessionDto, nowMs: Long): SyncKnowledge =
        if (session.version > appliedVersion) {
            copy(session = session, receivedAtMs = nowMs, appliedVersion = session.version)
        } else {
            this
        }

    fun withDevices(devices: List<PlaybackDevice>): SyncKnowledge = copy(devices = devices)

    /** The session's position as of [nowMs] on the same monotonic clock as [receivedAtMs]. */
    fun positionAt(nowMs: Long): Long {
        val session = session ?: return 0
        return extrapolatedPositionMs(
            positionMs = session.positionMs,
            durationMs = session.durationMs,
            isPlaying = session.isPlaying,
            playbackRate = session.playbackRate,
            elapsedMs = nowMs - receivedAtMs,
        )
    }
}

/**
 * Whether this device must stop because the session moved elsewhere: it is playing the session
 * here, and the session now names another device.
 *
 * Never while a claim of ours is still in flight — a session that predates the claim can arrive
 * after it was sent, and yielding to it would undo the tap that just started the music. Once the
 * claim is answered the rule is asked again against whatever is known by then.
 */
fun shouldYield(
    session: PlaybackSessionDto?,
    myDeviceId: String,
    playingSessionLocally: Boolean,
    claimInFlight: Boolean,
): Boolean {
    if (!playingSessionLocally || claimInFlight) return false
    val active = session?.activeDeviceId ?: return false
    return active != myDeviceId
}

/** 1 s, 2 s, 4 s … capped at 30 s: the stream's reconnect schedule after its [attempt]th failure. */
fun reconnectDelayMs(attempt: Int): Long {
    val shift = attempt.coerceIn(0, 5)
    return min(RECONNECT_CAP_MS, RECONNECT_BASE_MS shl shift)
}

private const val RECONNECT_BASE_MS = 1_000L
private const val RECONNECT_CAP_MS = 30_000L

/**
 * The stream's reconnect schedule as it runs: [reconnectDelayMs] over the failures since the last
 * connection that delivered its snapshot. That snapshot resets it, because the server ends every
 * stream after a few minutes on purpose — a routine end has to come back within a second, not
 * wherever the backoff had climbed to before.
 */
class ReconnectBackoff {
    private var failures = 0

    /** A connection delivered its snapshot: whatever ends it next starts the schedule over. */
    fun connected() {
        failures = 0
    }

    /** How long to wait before the next dial, counting this end as one more failure. */
    fun nextDelayMs(): Long = reconnectDelayMs(failures++)
}

/** What a command addressed to this device acts on; see [commandActionFor]. */
enum class CommandAction {
    /** This device's own player, as it is. */
    Player,

    /** The session has to be loaded first — the player holds nothing (its process was restarted). */
    AdoptFirst,

    /** A transfer: pick the session up here and play it. */
    Adopt,

    /** Not for what is playing here: a share queue is not the account's session. */
    Ignore,
}

/**
 * How this device answers a [command] addressed to it.
 *
 * A player holding a library queue acts on it, even when its song is not the one the server last
 * heard about: the phone moves on (a Next a moment ago, an auto-advance) a report's round trip
 * before the server knows, and re-loading the server's older copy then would undo that. Only a
 * player holding nothing loads the session first. A share queue is not the session, so a command
 * about the session leaves it alone — except a transfer, which asks for the account's music here.
 */
fun commandActionFor(command: String, loaded: Boolean, shareLoaded: Boolean, hasSession: Boolean): CommandAction =
    when {
        command == PlaybackCommand.TRANSFER -> CommandAction.Adopt
        shareLoaded -> CommandAction.Ignore
        // With nothing loaded, a pause is answered from what the server knows; nothing to load.
        command == PlaybackCommand.PAUSE || loaded || !hasSession -> CommandAction.Player
        else -> CommandAction.AdoptFirst
    }

/** The queue a device adopts: the rows it could resolve, and where the current song landed. */
data class Adoption<T>(val items: List<T>, val startIndex: Int)

/**
 * Resolves a session's queue against this device's own library, skipping what it cannot play.
 *
 * The entry at [queueIndex] is the session's song — [songId] wins over whatever the queue says
 * there, as it does on the web (`resolveAdoptQueue`). Null when that song is not here (or the index
 * is outside the queue, which the server never sends): adopting would start somewhere the listener
 * never was, so the caller says "This song isn't available here" instead.
 */
fun <T : Any> resolveAdoption(
    queue: List<Int>,
    queueIndex: Int,
    songId: Int,
    lookup: (Int) -> T?,
): Adoption<T>? {
    if (queueIndex !in queue.indices) return null
    val current = lookup(songId) ?: return null
    val items = ArrayList<T>(queue.size)
    var start = 0
    queue.forEachIndexed { index, id ->
        if (index == queueIndex) {
            start = items.size
            items += current
        } else {
            lookup(id)?.let { items += it }
        }
    }
    return Adoption(items, start)
}

/** Where an adoption starts: the session it picks up, at this position. */
data class AdoptionStart(val session: PlaybackSessionDto, val positionMs: Long)

/**
 * What an adoption picks up once it is done waiting — for the library to load, for the player — as
 * opposed to the copy of the session the tap saw ([requested]). A cold start's library load takes
 * seconds, and the device holding the session moves on meanwhile (a heartbeat, an auto-advance, a
 * skip on the Mac): starting from the tap's copy would start a song that device has left, or seconds
 * behind it. So the session is [latest]'s, at its position carried forward to [nowMs].
 *
 * A position the listener chose ([chosenPositionMs], a scrub on a remembered session) holds only for
 * the version it was chosen on. Null — nothing to adopt — when the session has no copy any more, or
 * has since come to this device some other way (it did not name this device when the tap happened).
 */
fun adoptionStartFor(
    requested: PlaybackSessionDto,
    latest: SyncKnowledge,
    myDeviceId: String,
    chosenPositionMs: Long?,
    nowMs: Long,
): AdoptionStart? {
    val session = latest.session ?: return null
    if (session.activeDeviceId == myDeviceId && requested.activeDeviceId != myDeviceId) return null
    val position = chosenPositionMs?.takeIf { session.version == requested.version } ?: latest.positionAt(nowMs)
    return AdoptionStart(session, position)
}

/** What a device reports when a snapshot arrives; see [snapshotReportFor]. */
enum class SnapshotReport {
    /** Nothing to say: the session is another device's (or none), or it is here and already paused. */
    None,

    /** The player as it is, now rather than at the next heartbeat. */
    Heartbeat,

    /** The session, paused where it has got to: it says this device plays it, and nothing of it is loaded here. */
    Stopped,
}

/**
 * What this device says when a snapshot names it as the one holding the session.
 *
 * - Holding the session's queue: a report at once ([SnapshotReport.Heartbeat]). The server treats a
 *   session it reloaded (after a restart) as paused until its device reports, and only a report keeps
 *   the holder reachable, so this is what makes it live again.
 * - Holding nothing of it while the session still says it plays here: the process died and came back
 *   within the server's reconnect grace, which the server cannot tell from a routine reconnect, so the
 *   session would carry on "playing" for another minute and be remembered that far past where the
 *   music stopped. Saying it stopped ([SnapshotReport.Stopped]) records the pause now.
 */
fun snapshotReportFor(session: PlaybackSessionDto?, myDeviceId: String, holdsLibraryQueue: Boolean): SnapshotReport =
    when {
        session == null || session.activeDeviceId != myDeviceId -> SnapshotReport.None
        holdsLibraryQueue -> SnapshotReport.Heartbeat
        session.isPlaying -> SnapshotReport.Stopped
        else -> SnapshotReport.None
    }

/**
 * The line under the song while it is not this device's: "Playing on MacBook", "Paused on
 * MacBook", "Last played on iPhone". Null in local mode.
 */
fun deviceLineFor(mode: PlaybackMode, deviceName: String?, isPlaying: Boolean): String? {
    val name = deviceName?.trim()?.takeIf(String::isNotEmpty)
    return when (mode) {
        PlaybackMode.Local -> null
        PlaybackMode.Remote -> "${if (isPlaying) "Playing" else "Paused"} on ${name ?: "another device"}"
        PlaybackMode.Remembered -> if (name != null) "Last played on $name" else "Last played"
    }
}

/**
 * Whether a command this device sent has landed: the active device acknowledged it, or — for a
 * transfer, which the target answers with a claim rather than an acknowledgement alone — the target
 * now holds the session.
 *
 * The second only counts when the target did not hold the session already when the command was sent
 * ([targetHeldSession]): a transfer to the remembered holder (it came back with nothing loaded) finds
 * it holding the session before it has done anything, so only its claim's acknowledgement says it
 * landed. Otherwise a pick-up that failed there would never say "Couldn't reach". The web's
 * `commandLanded` (`session.ts`).
 */
fun commandLanded(
    command: String,
    commandId: String,
    targetDeviceId: String?,
    targetHeldSession: Boolean,
    session: PlaybackSessionDto?,
): Boolean {
    if (session == null) return false
    if (session.lastCommandId == commandId) return true
    return command == PlaybackCommand.TRANSFER && !targetHeldSession &&
        targetDeviceId != null && session.activeDeviceId == targetDeviceId
}

/**
 * A command this device sent, waiting to see it land. [targetHeldSession]: the session named the
 * target as its device when the command was sent (see [commandLanded]).
 */
data class SentCommand(
    val commandId: String,
    val command: String,
    val targetDeviceId: String?,
    val targetHeldSession: Boolean,
)

/**
 * The ids of the commands still waiting ([pending], in the order they were sent) that have landed:
 * each one [commandLanded] says has, and every command sent to the same device before it. A device
 * carries its commands out in the order its stream delivers them, but a quick burst (three taps on
 * Next) is acknowledged by one report naming only the newest — and the server passes on only the
 * newest session anyway — so the older ones never show up as `lastCommandId` themselves. The web's
 * `landedCommands` (`session.ts`).
 */
fun landedCommands(session: PlaybackSessionDto?, pending: List<SentCommand>): List<String> {
    val landed = ArrayList<String>()
    val reached = HashSet<String?>()
    for (command in pending.asReversed()) {
        if (command.targetDeviceId !in reached &&
            !commandLanded(command.command, command.commandId, command.targetDeviceId, command.targetHeldSession, session)
        ) {
            continue
        }
        reached += command.targetDeviceId
        landed += command.commandId
    }
    return landed.asReversed()
}

/**
 * The commands this device sent and is waiting to see land — the web's `pending` and `reachedSeq`
 * (`playback-sync.svelte.ts`). Each is numbered when it is sent, before its POST, because the POSTs'
 * answers come back in any order: one answered after a later command to the same device landed has
 * landed too, and [landedCommands] alone could not tell, since that later one is no longer waiting.
 */
class SentCommands {
    private class Entry(val command: SentCommand, val seq: Long)

    private var lastSeq = 0L
    private val waiting = LinkedHashMap<String, Entry>()

    /** Per device, the newest command seen to land. */
    private val reachedSeq = HashMap<String?, Long>()

    /** The number of a command about to be sent: the order it was sent in. */
    fun number(): Long = ++lastSeq

    /** [command]'s POST was answered (as [seq]): it waits to land from now on. */
    fun watch(command: SentCommand, seq: Long) {
        waiting[command.commandId] = Entry(command, seq)
    }

    fun isWaiting(commandId: String): Boolean = commandId in waiting

    val isEmpty: Boolean get() = waiting.isEmpty()

    /** Settles every waiting command [session] shows has landed, and returns their ids. */
    fun settle(session: PlaybackSessionDto?): List<String> {
        val inOrder = waiting.values.sortedBy { it.seq }
        val landed = landedCommands(session, inOrder.map { it.command }).toHashSet()
        val settled = ArrayList<String>()
        for (entry in inOrder) {
            val target = entry.command.targetDeviceId
            val reached = reachedSeq[target] ?: 0L
            if (entry.command.commandId !in landed && entry.seq > reached) continue
            waiting.remove(entry.command.commandId)
            reachedSeq[target] = max(reached, entry.seq)
            settled += entry.command.commandId
        }
        return settled
    }

    /** [commandId]'s time is up: true when it was still waiting, which means it never landed. */
    fun giveUp(commandId: String): Boolean = waiting.remove(commandId) != null

    fun clear() {
        waiting.clear()
        reachedSeq.clear()
    }
}

/**
 * The session as it will look once [command] lands, shown while it is on its way: a remote pause
 * that took half a second to come back as a `session` event read as a button that ignored the tap,
 * and a seek snapped the scrubber back to where it was. [positionNowMs] is the extrapolated position
 * at the moment the command was sent. Next and previous cannot be predicted (the other device
 * owns the station), so they only freeze the clock at now.
 */
fun optimisticSession(
    session: PlaybackSessionDto,
    command: String,
    positionNowMs: Long,
    targetPositionMs: Long?,
): PlaybackSessionDto {
    val duration = session.durationMs?.takeIf { it > 0 }
    fun clamp(ms: Long) = ms.coerceAtLeast(0).let { if (duration != null) it.coerceAtMost(duration) else it }
    return when (command) {
        PlaybackCommand.PAUSE -> session.copy(isPlaying = false, positionMs = clamp(positionNowMs))
        PlaybackCommand.RESUME -> session.copy(isPlaying = true, positionMs = clamp(positionNowMs))
        PlaybackCommand.SEEK -> session.copy(positionMs = clamp(targetPositionMs ?: positionNowMs))
        else -> session.copy(positionMs = clamp(positionNowMs))
    }
}

/** How far a report's position may sit from where the last one said it would be, and still add nothing. */
const val REPORT_DRIFT_TOLERANCE_MS = 1_500L

/**
 * True when [next] would tell the server nothing it cannot work out from [last]: same song, same
 * state, no queue change, and a position within [REPORT_DRIFT_TOLERANCE_MS] of where [last]
 * extrapolates to by now.
 *
 * The player fires a burst of events for every stall (ready → buffering → ready), none of which
 * changes what the session says; each would otherwise bump the session's version and go out to
 * every device of the account. Claims and acknowledgements are never redundant — they are
 * statements, not state — and the caller exempts heartbeats, whose job is to be sent.
 */
fun isRedundantReport(
    last: PlaybackStateReport?,
    lastSentAtMs: Long,
    next: PlaybackStateReport,
    nowMs: Long,
): Boolean {
    if (last == null || next.claim || next.inResponseTo != null || next.queue != null) return false
    fun PlaybackStateReport.stateOnly() = copy(claim = false, inResponseTo = null, queue = null, positionMs = 0)
    if (last.stateOnly() != next.stateOnly()) return false
    val expected = extrapolatedPositionMs(
        positionMs = last.positionMs,
        durationMs = last.durationMs,
        isPlaying = last.isPlaying,
        playbackRate = last.playbackRate,
        elapsedMs = nowMs - lastSentAtMs,
    )
    return kotlin.math.abs(next.positionMs - expected) <= REPORT_DRIFT_TOLERANCE_MS
}

/** One of the other devices in the picker. [status] is "Playing" / "Paused" on the one holding it. */
data class DevicePickerRow(
    val deviceId: String,
    val name: String,
    val kind: String,
    /** Holds the music right now: checked, and listed first. */
    val current: Boolean,
    val status: String?,
)

/** The device picker: "This device" first (checked while the music is here), then the rest. */
data class DevicePicker(
    val thisDeviceCurrent: Boolean = false,
    val others: List<DevicePickerRow> = emptyList(),
)

/**
 * Builds the picker the way the web's does (`playback-sync.svelte.ts`'s `entries`), so the two
 * list the same devices in the same order: this device is left out of the list (it is always the
 * first row, "This device", checked in local mode); the others are the ones online now, the one
 * holding a live session checked and marked "Playing" or "Paused", then by name.
 */
fun devicePickerFor(
    devices: List<PlaybackDevice>,
    session: PlaybackSessionDto?,
    myDeviceId: String,
    mode: PlaybackMode,
): DevicePicker {
    val others = devices
        .filter { it.deviceId != myDeviceId && it.online }
        .map { device ->
            val holds = mode == PlaybackMode.Remote && session?.activeDeviceId == device.deviceId
            DevicePickerRow(
                deviceId = device.deviceId,
                name = device.name,
                kind = device.kind,
                current = holds,
                status = if (!holds) null else if (session?.isPlaying == true) "Playing" else "Paused",
            )
        }
        .sortedWith(compareByDescending<DevicePickerRow> { it.current }.thenBy(String.CASE_INSENSITIVE_ORDER) { it.name })
    return DevicePicker(thisDeviceCurrent = mode == PlaybackMode.Local, others = others)
}
