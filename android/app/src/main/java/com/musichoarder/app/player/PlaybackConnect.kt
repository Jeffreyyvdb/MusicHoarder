package com.musichoarder.app.player

import android.os.SystemClock
import androidx.media3.common.ForwardingPlayer
import androidx.media3.common.MediaItem
import androidx.media3.common.Player
import com.musichoarder.app.data.ApiException
import com.musichoarder.app.data.CommandAction
import com.musichoarder.app.data.DeviceIdentity
import com.musichoarder.app.data.DevicePicker
import com.musichoarder.app.data.LibraryRepository
import com.musichoarder.app.data.MusicHoarderApi
import com.musichoarder.app.data.NotPairedException
import com.musichoarder.app.data.PlaybackCommand
import com.musichoarder.app.data.PlaybackCommandBody
import com.musichoarder.app.data.PlaybackCommandEvent
import com.musichoarder.app.data.PlaybackCommandResult
import com.musichoarder.app.data.PlaybackDevicesEvent
import com.musichoarder.app.data.PlaybackEvent
import com.musichoarder.app.data.PlaybackJson
import com.musichoarder.app.data.PlaybackMode
import com.musichoarder.app.data.PlaybackSessionDto
import com.musichoarder.app.data.PlaybackSessionEvent
import com.musichoarder.app.data.PlaybackSnapshot
import com.musichoarder.app.data.PlaybackStateReport
import com.musichoarder.app.data.PlaybackStateResponse
import com.musichoarder.app.data.ReconnectBackoff
import com.musichoarder.app.data.SentCommand
import com.musichoarder.app.data.SentCommands
import com.musichoarder.app.data.SessionStore
import com.musichoarder.app.data.SnapshotReport
import com.musichoarder.app.data.SseEvent
import com.musichoarder.app.data.SyncKnowledge
import com.musichoarder.app.data.adoptionStartFor
import com.musichoarder.app.data.commandActionFor
import com.musichoarder.app.data.devicePickerFor
import com.musichoarder.app.data.displayModeFor
import com.musichoarder.app.data.isRedundantReport
import com.musichoarder.app.data.optimisticSession
import com.musichoarder.app.data.playbackModeFor
import com.musichoarder.app.data.reconnectDelayMs
import com.musichoarder.app.data.resolveAdoption
import com.musichoarder.app.data.shouldYield
import com.musichoarder.app.data.snapshotReportFor
import com.musichoarder.app.data.trimQueue
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.filterNotNull
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.flow.transformLatest
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import java.io.IOException

/**
 * What the player UI needs from playback sync: which mode it is in, and the session and devices
 * behind that. [session] is null whenever the feature is off (see [available]).
 */
data class ConnectView(
    /**
     * Signed in, not the demo, the server has answered a playback call ([SyncSupport.shown]), and no
     * share queue is loaded here.
     */
    val available: Boolean = false,
    val mode: PlaybackMode = PlaybackMode.Local,
    /** With any optimistic command already folded in, so the transport reacts to its own tap. */
    val knowledge: SyncKnowledge = SyncKnowledge(),
    val myDeviceId: String = "",
    /** The stream has delivered its snapshot and is still open. */
    val connected: Boolean = false,
) {
    val session: PlaybackSessionDto? get() = if (available) knowledge.session else null

    val picker: DevicePicker
        get() = if (available) devicePickerFor(knowledge.devices, knowledge.session, myDeviceId, mode) else DevicePicker()

    /**
     * Now Playing's Devices button: whenever the feature is on, as the web's is — a lone phone
     * playing its own music included, whose picker then says how other devices appear.
     */
    val showsDevices: Boolean get() = available
}

/**
 * What this phone knows of its server's playback sync. A self-hosted server can be older than the
 * app, and is upgraded (or rolled back) under it, so the feature is shown only once the server has
 * answered a playback call — never merely because it has not refused one yet, which is true at every
 * start and again at every retry after a 404, and made the Devices button appear and vanish again.
 */
internal enum class SyncSupport {
    /** Not asked yet, or being asked again after a 404: calls go out, nothing shows until one is answered. */
    Unknown,

    /** A playback call was answered. */
    Supported,

    /** 404: the server predates the feature. Asked again after a while — servers get upgraded. */
    Unsupported,

    /** 403: the account may not use this (the demo, which strangers share). Until it changes. */
    Forbidden,
    ;

    /** Worth calling the server: the stream is opened and reports are sent. */
    val callsServer: Boolean get() = this == Unknown || this == Supported

    /** The feature shows: the Devices button, the picker, another device's session. */
    val shown: Boolean get() = this == Supported
}

/** A message for the snackbar, optionally with a "Play here" action. */
data class ConnectNotice(val message: String, val offerPlayHere: Boolean = false)

/**
 * Playback sync ("Connect"): the account's one playback session, shared by every device signed in
 * to it. Application-scoped, because the part that matters most has to work with the Activity gone
 * — a phone playing in a pocket must still stop when the Mac takes over.
 *
 * It talks to the **service's own ExoPlayer**, attached by [PlaybackService], never to the app's
 * MediaController:
 *
 * - **Reports.** Every play/pause, seek, track, queue, rate or shuffle change is reported while this
 *   device holds the session, plus a heartbeat every 20 s. A *claim* — "this device is the one
 *   playing now" — is sent only for a local play intent, which is anything that reaches the player
 *   through the media session: a tap in the app, the notification, the lock screen, a headset.
 *   [sessionPlayer] is the wrapper that notices them. Share queues are never reported; leaving the
 *   session for one reports it paused, once.
 * - **Media keys pick the session up.** While another device's session (or a remembered one) is
 *   shown, a play, next or previous from the notification, the lock screen or a headset adopts it
 *   here, as the web's media keys do — never the hidden leftovers the player still holds. With the
 *   stream closed, what is shown may be hours old, so the server is asked first ([_connected]).
 * - **Yielding.** When the session moves to another device while this one plays it, the player is
 *   paused here (and a snackbar says where it went, when the app is up to show one).
 * - **Commands.** Pause, resume, next, previous and seek from another device act on the player and
 *   are acknowledged; a transfer adopts the session and claims it.
 * - **Remote control.** While another device holds the session, the UI's transport sends it
 *   commands instead ([sendCommand]), and Play here / This device adopts it ([playHere]).
 *
 * **No feedback loop**, by construction: what this class does to the player (a yield, a command)
 * goes to the raw ExoPlayer, around the wrapper, so it is never mistaken for a local intent; the
 * reports it causes are sent only while this device holds the session; and incoming session events
 * never touch the player except to pause it. Everything here runs on the main thread, which is the
 * player's own.
 *
 * The event stream stays open while the app's UI is started, or while the service is playing (or
 * holds the session, for ten minutes after it pauses — long enough for "resume" from the Mac to
 * reach a phone that was paused a moment ago, short enough not to hold a connection open all night).
 */
@OptIn(ExperimentalCoroutinesApi::class)
class PlaybackConnect(
    private val api: MusicHoarderApi,
    private val sessions: SessionStore,
    private val library: LibraryRepository,
    private val identity: DeviceIdentity,
    /** Monotonic: positions are carried forward by elapsed time, and a wall clock can step back. */
    private val clock: () -> Long = SystemClock::elapsedRealtime,
) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val myDeviceId: String get() = identity.deviceId

    // ---- State. Main thread only. --------------------------------------------------------------

    private data class LocalStatus(
        val attached: Boolean = false,
        val loaded: Boolean = false,
        val shareQueue: Boolean = false,
        /** Playing, or about to (buffering towards it). */
        val playing: Boolean = false,
    )

    /** What the session will look like once a command lands, until a newer session says otherwise. */
    private data class Optimistic(val session: PlaybackSessionDto, val atMs: Long, val baseVersion: Long)

    private data class ReportRequest(
        /** How many local play intents this report answers. A claim is sent only if the player plays. */
        val claims: Int = 0,
        val inResponseTo: String? = null,
        /** Send even without holding the session: a snapshot doubted it, and the answer settles it. */
        val verify: Boolean = false,
        /** Sent however little changed — it is what keeps this device listed as reachable. */
        val heartbeat: Boolean = false,
        /**
         * The session says this device plays it and nothing of it is loaded here: report it paused,
         * from what the server knows ([SnapshotReport.Stopped]). A loaded player speaks for itself.
         */
        val stopped: Boolean = false,
    ) {
        fun mergedWith(next: ReportRequest) = ReportRequest(
            claims = claims + next.claims,
            inResponseTo = inResponseTo ?: next.inResponseTo,
            verify = verify || next.verify,
            heartbeat = heartbeat || next.heartbeat,
            stopped = stopped || next.stopped,
        )
    }

    private val _knowledge = MutableStateFlow(SyncKnowledge())
    private val _optimistic = MutableStateFlow<Optimistic?>(null)
    private val _local = MutableStateFlow(LocalStatus())
    /** Claims requested and not yet answered. While any is out, the local player is the truth. */
    private val _claims = MutableStateFlow(0)

    /**
     * The stream is open and has delivered its snapshot — which is also what keeps [_knowledge]
     * current. Once it closes (and a phone that stopped playing closes it on purpose), what we know
     * is only what it said before: the session may have moved to another device since, or moved
     * on there, or come to rest. Nothing that acts on the account's session from here — a media
     * key's pick-up, an adoption — trusts it without asking the server first ([checkWithServer]). A
     * snapshot fetched meanwhile answers the question of the moment, and does not set this: nothing
     * keeps it current either.
     */
    private val _connected = MutableStateFlow(false)
    private val _uiStarted = MutableStateFlow(false)
    private val _lingerExpired = MutableStateFlow(false)
    private val _support = MutableStateFlow(SyncSupport.Unknown)
    private val _player = MutableStateFlow<Player?>(null)
    private val player: Player? get() = _player.value

    /** Commands this device sent, waiting to land; each has its timeout in [commandTimeouts]. */
    private val sentCommands = SentCommands()
    private val commandTimeouts = HashMap<String, Job>()
    private var unsupportedRetry: Job? = null

    /** The device a transfer of ours went to, so losing the session to it is not news. */
    private var transferredTo: String? = null
    private var transferForget: Job? = null

    /** The adopted current song, whose start must not count as a new listen. */
    private var adoptedSongId: Int? = null

    /** Bumped by every local intent, so an adoption that was overtaken by a tap gives way. */
    private var localIntentGeneration = 0

    /**
     * Keys pressed while the server is being asked where the session is ([pickUpChecked]), in the
     * order they were pressed, for the question asked in [generation]. Later keys join this one
     * question rather than overtaking it, so two quick Nexts on a slow radio are still two.
     */
    private class PendingKeys(val generation: Int) {
        val keys = mutableListOf<Pair<HereAction, () -> Unit>>()
    }

    private var pendingKeys: PendingKeys? = null

    /** The knowledge came from a snapshot nothing has confirmed yet; see [afterKnowledgeChanged]. */
    private var snapshotUnconfirmed = false

    private var lastSent: PlaybackStateReport? = null
    private var lastSentAtMs = 0L

    /**
     * The session's song as it stood when a controller swapped the queue for a share link's while
     * this device held the session: reported once, paused, so the session stops saying this phone
     * plays it. The share itself is not the account's session and is never reported.
     */
    private var leftForShare: PlaybackStateReport? = null

    private val reports = Channel<ReportRequest>(Channel.UNLIMITED)

    private val _notices = MutableSharedFlow<ConnectNotice>(
        extraBufferCapacity = 4,
        onBufferOverflow = BufferOverflow.DROP_OLDEST,
    )

    /** Snackbar material: the session moved away, a device could not be reached, a song is missing. */
    val notices: SharedFlow<ConnectNotice> = _notices.asSharedFlow()

    private val _adoptions = MutableSharedFlow<Unit>(extraBufferCapacity = 1, onBufferOverflow = BufferOverflow.DROP_OLDEST)

    /** Fires when an adopted session replaced the local queue, so the UI can drop share-queue state. */
    val adoptions: SharedFlow<Unit> = _adoptions.asSharedFlow()

    /** Signed in, not the demo account, and not refused by the server: worth calling it. */
    private val enabled: StateFlow<Boolean> = combine(sessions.session, _support) { session, support ->
        session != null && !session.role.equals(DEMO_ROLE, ignoreCase = true) && support.callsServer
    }.stateIn(scope, SharingStarted.Eagerly, false)

    /** [enabled], and the server has answered: the feature shows. */
    private val shown: Flow<Boolean> = combine(enabled, _support) { enabled, support -> enabled && support.shown }

    private val accountKey: StateFlow<String?> = sessions.session
        .map { session -> session?.let { "${it.baseUrl}\n${it.token}" } }
        .stateIn(scope, SharingStarted.Eagerly, null)

    val view: StateFlow<ConnectView> = combine(
        combine(_knowledge, _optimistic) { knowledge, optimistic -> effectiveKnowledge(knowledge, optimistic) },
        _local,
        _claims,
        shown,
        _connected,
    ) { knowledge, local, claims, shown, connected ->
        viewOf(knowledge, local, claims, shown, connected)
    }.stateIn(scope, SharingStarted.Eagerly, ConnectView(myDeviceId = myDeviceId))

    private fun viewOf(knowledge: SyncKnowledge, local: LocalStatus, claims: Int, shown: Boolean, connected: Boolean): ConnectView {
        val available = shown && !local.shareQueue
        val session = if (available) knowledge.session else null
        val mode = displayModeFor(
            mode = playbackModeFor(session, myDeviceId, available),
            session = session,
            myDeviceId = myDeviceId,
            localLoaded = local.loaded,
            claimInFlight = claims > 0,
        )
        return ConnectView(available, mode, knowledge, myDeviceId, connected)
    }

    /** [view] as of this very moment, for a decision made right after the knowledge changed under it. */
    private fun viewNow(): ConnectView = viewOf(
        effectiveKnowledge(_knowledge.value, _optimistic.value),
        _local.value,
        _claims.value,
        enabled.value && _support.value.shown,
        _connected.value,
    )

    init {
        // A different account (or a sign-out) starts from nothing: another account's session, its
        // devices and whatever its server refused are none of this one's business.
        scope.launch {
            accountKey.collect { resetForAccount() }
        }

        // The stream: open while wanted, reopened for a new account. The close is deferred a few
        // seconds so a rotation (stop, then start again a moment later) does not drop and redial.
        val wanted = combine(
            enabled,
            _uiStarted,
            _local,
            _knowledge.map { it.session?.activeDeviceId == myDeviceId }.distinctUntilChanged(),
            _lingerExpired,
        ) { enabled, uiStarted, local, holds, lingerExpired ->
            val servicePlaying = local.attached && local.loaded && !local.shareQueue &&
                (local.playing || (holds && !lingerExpired))
            enabled && (uiStarted || servicePlaying)
        }
            .distinctUntilChanged()
            .transformLatest { want ->
                if (!want) delay(STREAM_CLOSE_GRACE_MS)
                emit(want)
            }
            .distinctUntilChanged()
        scope.launch {
            combine(accountKey, wanted) { key, want -> key.takeIf { want } }
                .distinctUntilChanged()
                .collectLatest { key -> if (key != null) runStream() }
        }

        // The ten-minute linger after a pause, for the rule above.
        scope.launch {
            _local.map { it.playing }.distinctUntilChanged().collectLatest { playing ->
                _lingerExpired.value = false
                if (!playing) {
                    delay(PAUSED_LINGER_MS)
                    _lingerExpired.value = true
                }
            }
        }

        // The heartbeat: every 20 s while this device holds the session and the stream is up.
        scope.launch {
            combine(_connected, _knowledge, _local) { connected, knowledge, local ->
                connected && knowledge.session?.activeDeviceId == myDeviceId &&
                    local.attached && local.loaded && !local.shareQueue
            }
                .distinctUntilChanged()
                .collectLatest { beating ->
                    if (!beating) return@collectLatest
                    while (true) {
                        delay(HEARTBEAT_MS)
                        requestReport(ReportRequest(heartbeat = true))
                    }
                }
        }

        scope.launch { runReportLoop() }
    }

    /** The session with an optimistic command folded in, until any newer version arrives. */
    private fun effectiveKnowledge(knowledge: SyncKnowledge, optimistic: Optimistic?): SyncKnowledge =
        if (optimistic != null && optimistic.baseVersion == knowledge.appliedVersion) {
            knowledge.copy(session = optimistic.session, receivedAtMs = optimistic.atMs)
        } else {
            knowledge
        }

    // ---- Wiring from the service and the Activity ---------------------------------------------

    /**
     * The player the media session should drive: [player] with every local play intent noticed on
     * the way through. The app's controller, the notification, the lock screen and a headset all
     * reach the player through the session, so all of them go through here — and nothing this class
     * does to the player itself does, which is what keeps a yield from counting as a claim.
     */
    fun sessionPlayer(player: Player): Player = LocalIntentPlayer(player, localIntents)

    private val localIntents = object : LocalIntents {
        override fun onIntent(play: Boolean) = onLocalIntent(play)

        // A pause is the last word: whatever was still waiting on the server — a Play, a pick-up
        // — gives way to it rather than starting the music after all.
        override fun onPause() {
            localIntentGeneration++
            pendingKeys = null
        }

        override fun pickUpInstead(action: HereAction, local: () -> Unit): Boolean = pickUpSessionInstead(action, local)
        override fun beforeQueueReplaced() = noteLeavingSession()
    }

    fun attach(player: Player) {
        _player.value?.removeListener(listener)
        _player.value = player
        player.addListener(listener)
        refreshLocal(player)
    }

    fun detach(player: Player) {
        if (_player.value !== player) return
        player.removeListener(listener)
        _player.value = null
        _local.value = LocalStatus()
    }

    /** The app's UI is started (visible) or stopped: the stream follows it. */
    fun setUiStarted(started: Boolean) {
        _uiStarted.value = started
    }

    /** This phone's name and icon, for the picker's "This device" row. */
    val thisDeviceName: String get() = identity.name
    val thisDeviceKind: String get() = identity.kind

    /**
     * Whether [songId] starting just now is the adopted session's current song, which must not
     * count as a new listen — the web's reload restore skips its restored track for the same
     * reason. Answers once.
     */
    fun consumeAdoptedStart(songId: Int): Boolean {
        if (adoptedSongId != songId) return false
        adoptedSongId = null
        return true
    }

    // ---- From the UI --------------------------------------------------------------------------

    /** What "Play here" should do beyond starting the session at its saved position. */
    enum class HereAction { Resume, Next, Previous }

    /**
     * Adopts the session on this device — "This device", Play here, and Play (or next, previous,
     * a seek) while the session is only remembered. A local play intent, so it claims.
     */
    fun playHere(action: HereAction = HereAction.Resume, positionMs: Long? = null) {
        val session = view.value.session ?: return
        scope.launch { adoptHere(session, action, positionMs) }
    }

    /**
     * [playHere], once there is a session to pick up. [knowledgeChecked]: see [adopt]. [then]: more
     * keys pressed on the way, taken as further steps once it is here.
     */
    private suspend fun adoptHere(
        session: PlaybackSessionDto,
        action: HereAction,
        positionMs: Long? = null,
        knowledgeChecked: Boolean = false,
        then: List<HereAction> = emptyList(),
    ) {
        if (!adopt(session, play = true, inResponseTo = null, positionMs = positionMs, knowledgeChecked = knowledgeChecked)) return
        val player = player ?: return
        for (step in listOf(action) + then) {
            when (step) {
                HereAction.Resume -> Unit
                HereAction.Next -> player.seekToNextMediaItem()
                HereAction.Previous -> player.seekToPrevious()
            }
        }
    }

    /** Pause, resume, next, previous or seek on the device holding the session. */
    fun sendCommand(command: String, positionMs: Long? = null) {
        val current = view.value
        val session = current.session ?: return
        val target = session.activeDeviceId ?: return
        // The transport answers the tap at once; the session event that confirms it takes a
        // round trip through the other device.
        _optimistic.value = Optimistic(
            session = optimisticSession(session, command, current.knowledge.positionAt(clock()), positionMs),
            atMs = clock(),
            baseVersion = _knowledge.value.appliedVersion,
        )
        postCommand(PlaybackCommandBody(myDeviceId, target, command, positionMs), nameOf(target))
    }

    /** What a snackbar calls [deviceId]: its listed name, else the session's name for it. */
    private fun nameOf(deviceId: String): String? {
        val knowledge = _knowledge.value
        knowledge.devices.firstOrNull { it.deviceId == deviceId }?.name?.takeIf(String::isNotBlank)?.let { return it }
        return knowledge.session?.takeIf { it.activeDeviceId == deviceId }?.activeDeviceName
    }

    /** Asks [deviceId] to take the session over. It keeps playing here until that device claims. */
    fun transferTo(deviceId: String) {
        val device = _knowledge.value.devices.firstOrNull { it.deviceId == deviceId } ?: return
        transferredTo = deviceId
        transferForget?.cancel()
        transferForget = scope.launch {
            delay(TRANSFER_MEMORY_MS)
            transferredTo = null
        }
        postCommand(PlaybackCommandBody(myDeviceId, deviceId, PlaybackCommand.TRANSFER, null), device.name)
    }

    private fun postCommand(body: PlaybackCommandBody, targetName: String?) {
        // Numbered now, in the order the taps happened: the answers can come back in any order.
        val seq = sentCommands.number()
        // And whether the target already held the session, which the answer may be too late to
        // tell: a transfer to it cannot then land merely by it holding the session.
        val targetHeldSession = _knowledge.value.session?.activeDeviceId == body.targetDeviceId
        val account = accountKey.value
        scope.launch {
            val result = try {
                api.sendPlaybackCommand(body)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                PlaybackCommandResult.Refused(0, null)
            }
            // Signed into another account while this was in flight: the answer is not ours.
            if (accountKey.value != account) return@launch
            when (result) {
                is PlaybackCommandResult.Sent -> {
                    _support.value = SyncSupport.Supported
                    track(SentCommand(result.commandId, body.command, body.targetDeviceId, targetHeldSession), seq, targetName)
                }
                is PlaybackCommandResult.Refused -> {
                    _optimistic.value = null
                    when (result.error) {
                        // What we believed has moved on: catch up rather than say something wrong.
                        ERROR_NOT_ACTIVE_DEVICE, ERROR_NO_SESSION -> refreshKnowledge()
                        else -> unreachable(body.command, targetName)
                    }
                }
            }
        }
    }

    /**
     * Waits for [command] to land, alongside any other command still on its way: each has its own
     * five seconds, and one sent before a command that landed has landed too (see [SentCommands]).
     */
    private fun track(command: SentCommand, seq: Long, targetName: String?) {
        sentCommands.watch(command, seq)
        // Its acknowledgement (or a later command's) can beat the command's own answer here.
        settleCommands()
        if (!sentCommands.isWaiting(command.commandId)) return
        commandTimeouts[command.commandId] = scope.launch {
            delay(COMMAND_TIMEOUT_MS)
            commandTimeouts.remove(command.commandId)
            if (!sentCommands.giveUp(command.commandId)) return@launch
            // The optimistic session is the newest command's: it stays while any command is out.
            if (sentCommands.isEmpty) _optimistic.value = null
            unreachable(command.command, targetName)
        }
    }

    /** Settles every command the session now shows has landed. */
    private fun settleCommands() {
        for (commandId in sentCommands.settle(_knowledge.value.session)) commandTimeouts.remove(commandId)?.cancel()
    }

    private fun forgetCommands() {
        sentCommands.clear()
        commandTimeouts.values.forEach(Job::cancel)
        commandTimeouts.clear()
    }

    /**
     * "Couldn’t reach MacBook". Resume and transfer were asking for music, so they offer it here
     * instead — unless it is already playing here (a transfer that did not land leaves it so).
     */
    private fun unreachable(command: String, targetName: String?) {
        val offer = (command == PlaybackCommand.RESUME || command == PlaybackCommand.TRANSFER) &&
            player?.playsSessionHere() != true
        _notices.tryEmit(ConnectNotice("Couldn’t reach ${targetName?.takeIf(String::isNotBlank) ?: "the other device"}", offer))
    }

    /** Asks the server for the session and its devices; true once the answer is what we know. */
    private suspend fun refreshKnowledge(): Boolean {
        val account = accountKey.value
        val snapshot = try {
            api.fetchPlayback()
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            return false
        }
        // Signed into another account while this was in flight: the answer is not ours.
        if (accountKey.value != account) return false
        applySnapshot(snapshot, fromStream = false)
        return true
    }

    /**
     * [refreshKnowledge] for a decision that is waiting on it — a media key, an adoption — so it
     * waits no longer than [SERVER_CHECK_MS], not for as long as the HTTP client would on a dead
     * network. An answer that comes after that is still applied; it is only too late for this.
     */
    private suspend fun checkWithServer(): Boolean {
        val answer = scope.async { refreshKnowledge() }
        return withTimeoutOrNull(SERVER_CHECK_MS) { answer.await() } ?: false
    }

    // ---- The stream ---------------------------------------------------------------------------

    private suspend fun runStream() {
        val backoff = ReconnectBackoff()
        try {
            while (true) {
                try {
                    api.playbackStream(
                        deviceId = identity.deviceId,
                        installId = identity.installId,
                        name = identity.name,
                        kind = identity.kind,
                        clientName = identity.client,
                    ).collect { event ->
                        if (event.event == PlaybackEvent.SNAPSHOT) backoff.connected()
                        onStreamEvent(event)
                    }
                    // Ended cleanly (the server ends every stream after a few minutes, a deploy,
                    // a proxy recycling the connection): come back — within a second, since the
                    // snapshot reset the schedule.
                } catch (e: CancellationException) {
                    throw e
                } catch (e: ApiException) {
                    when (e.status) {
                        403 -> return refuse(SyncSupport.Forbidden)
                        404 -> return refuse(SyncSupport.Unsupported)
                    }
                } catch (e: NotPairedException) {
                    return
                } catch (e: Exception) {
                    // A dropped connection, a 401 the library will evict the account over, a
                    // server restarting: all the same to us — wait, then dial again.
                }
                _connected.value = false
                delay(backoff.nextDelayMs())
            }
        } finally {
            // Closed: what we know stops being kept current here (see _connected).
            _connected.value = false
        }
    }

    private fun onStreamEvent(event: SseEvent) {
        when (event.event) {
            PlaybackEvent.SNAPSHOT -> decode<PlaybackSnapshot>(event.data)?.let { applySnapshot(it, fromStream = true) }
            PlaybackEvent.SESSION -> decode<PlaybackSessionEvent>(event.data)?.session?.let(::applySession)
            PlaybackEvent.DEVICES -> decode<PlaybackDevicesEvent>(event.data)?.let { devices ->
                _knowledge.update { it.withDevices(devices.devices) }
            }
            PlaybackEvent.COMMAND -> decode<PlaybackCommandEvent>(event.data)?.let { command ->
                scope.launch { execute(command) }
            }
            // `ping` only keeps the connection warm.
        }
    }

    private inline fun <reified T> decode(data: String): T? =
        runCatching { PlaybackJson.decodeFromString<T>(data) }.getOrNull()

    /** [fromStream]: the stream's own first word, which it then keeps current ([_connected]). */
    private fun applySnapshot(snapshot: PlaybackSnapshot, fromStream: Boolean) {
        _support.value = SyncSupport.Supported
        _knowledge.value = _knowledge.value.withSnapshot(snapshot, clock())
        _optimistic.value = null
        if (fromStream) _connected.value = true
        snapshotUnconfirmed = true
        // After a server restart nothing it had from us can be assumed — the next report re-sends.
        lastSent = null
        afterKnowledgeChanged()
        // Still named as the holder: say now what is true here, rather than at the next heartbeat
        // (or never, with nothing loaded to heartbeat about).
        when (snapshotReportFor(snapshot.session, myDeviceId, player?.holdsLibraryQueue() == true)) {
            SnapshotReport.Heartbeat -> requestReport(ReportRequest(heartbeat = true))
            SnapshotReport.Stopped -> requestReport(ReportRequest(stopped = true))
            SnapshotReport.None -> Unit
        }
    }

    private fun applySession(session: PlaybackSessionDto) {
        val before = _knowledge.value
        val after = before.withSession(session, clock())
        if (after === before) return
        _knowledge.value = after
        snapshotUnconfirmed = false
        _optimistic.value?.let { if (after.appliedVersion > it.baseVersion) _optimistic.value = null }
        afterKnowledgeChanged()
    }

    /**
     * The two things a change in what we know can mean: a command of ours landed, or the session
     * moved away while it plays here.
     *
     * A move that only a snapshot reports is checked with the server before acting on it. A
     * snapshot is written the moment a stream connects, so one that raced a claim of ours can
     * arrive after the claim was answered and describe the world just before it — pausing on that
     * would undo the tap that started the music. The check is a report, whose answer is
     * authoritative: accepted means this device still holds it.
     */
    private fun afterKnowledgeChanged() {
        val session = _knowledge.value.session
        val player = player
        if (enabled.value && player != null &&
            shouldYield(session, myDeviceId, player.playsSessionHere(), _claims.value > 0)
        ) {
            if (snapshotUnconfirmed) requestReport(ReportRequest(verify = true)) else yieldTo(session)
        }
        settleCommands()
    }

    /** Stops here: the session belongs to another device now. */
    private fun yieldTo(session: PlaybackSessionDto?) {
        val player = player ?: return
        if (!player.playsSessionHere()) return
        player.pause()
        val movedTo = session?.activeDeviceId
        if (movedTo != null && movedTo == transferredTo) return
        val name = session?.activeDeviceName?.takeIf(String::isNotBlank) ?: "another device"
        _notices.tryEmit(ConnectNotice("Now playing on $name", offerPlayHere = true))
    }

    private fun refuse(refusal: SyncSupport) {
        _support.value = refusal
        if (refusal == SyncSupport.Unsupported) {
            unsupportedRetry?.cancel()
            unsupportedRetry = scope.launch {
                delay(UNSUPPORTED_RETRY_MS)
                // Asked again, but not shown again until it answers: it most likely still 404s.
                if (_support.value == SyncSupport.Unsupported) _support.value = SyncSupport.Unknown
            }
        }
    }

    private fun resetForAccount() {
        _knowledge.value = SyncKnowledge()
        _optimistic.value = null
        _connected.value = false
        _support.value = SyncSupport.Unknown
        unsupportedRetry?.cancel()
        forgetCommands()
        // An adoption still waiting (on the library, on the player) was for the other account.
        localIntentGeneration++
        transferredTo = null
        adoptedSongId = null
        snapshotUnconfirmed = false
        lastSent = null
        leftForShare = null
    }

    // ---- Commands addressed to this device -----------------------------------------------------

    private suspend fun execute(command: PlaybackCommandEvent) {
        // Counted from here: a tap, or another account, while this waits makes an adoption moot.
        val generation = localIntentGeneration
        val player = awaitPlayer() ?: return
        val session = _knowledge.value.session
        val loaded = player.mediaItemCount > 0
        val action = commandActionFor(command.command, loaded, loaded && player.isShareItem, session != null)
        val ack = ReportRequest(inResponseTo = command.commandId)
        when (action) {
            CommandAction.Ignore -> return

            CommandAction.Adopt -> {
                // Asked for when the stream has not said: either way it becomes what this device
                // knows, received now, so the adoption carries it forward from here.
                if (session == null) refreshKnowledge()
                val current = _knowledge.value.session ?: return
                adopt(current, play = true, inResponseTo = command.commandId, since = generation)
                return
            }

            // Nothing loaded — the process was restarted since this device took the session — so
            // the command is about a queue that has to be loaded first.
            CommandAction.AdoptFirst -> if (session != null) {
                // A resume is the adoption itself, and its claim the acknowledgement.
                if (command.command == PlaybackCommand.RESUME) {
                    adopt(session, play = true, inResponseTo = command.commandId, since = generation)
                    return
                }
                if (!adopt(session, play = false, inResponseTo = null, since = generation)) return
            }

            CommandAction.Player -> Unit
        }
        when (command.command) {
            PlaybackCommand.PAUSE -> player.pause()
            PlaybackCommand.RESUME -> {
                player.resumePlayback()
                requestReport(ack.copy(claims = 1))
                return
            }
            PlaybackCommand.NEXT -> player.seekToNextMediaItem()
            PlaybackCommand.PREVIOUS -> player.seekToPrevious()
            PlaybackCommand.SEEK -> command.positionMs?.let { player.seekTo(it.coerceAtLeast(0)) }
            else -> return
        }
        requestReport(ack)
    }

    /**
     * Takes the session over on this device: its queue resolved against this phone's library
     * (skipping what is not here), its station seed and shuffle, at its position carried forward
     * to now. Returns false — with a snackbar — when the current song itself is not here.
     *
     * [session] is the copy the tap (or the command) saw; what is adopted is the session as it
     * stands once the waits below are over ([adoptionStartFor]). A tap that lands while they last
     * wins, and so does signing into another account: the adoption gives way. [since] is the
     * [localIntentGeneration] the request was made in, for a caller that waited before this.
     *
     * With the stream closed, what we know may be hours old, so the server is asked where the
     * session is first — unless the caller has only just asked ([knowledgeChecked]).
     */
    private suspend fun adopt(
        session: PlaybackSessionDto,
        play: Boolean,
        inResponseTo: String?,
        positionMs: Long? = null,
        since: Int = localIntentGeneration,
        knowledgeChecked: Boolean = false,
    ): Boolean {
        val account = accountKey.value
        // Every row the library holds, not just the Tracks list's — the web resolves against all of
        // `/songs` too. Loaded first when it is not yet, so a queue is never cut short (and then
        // claimed short) only because the library was still on its way.
        if (library.state.value.songsById.isEmpty()) library.refresh()
        val player = awaitPlayer() ?: return false
        // Carried forward from where the stream left it, a song that played on for an hour since
        // is at its end — and the claim that follows would make that copy the account's session.
        if (!knowledgeChecked && !_connected.value) checkWithServer()
        if (since != localIntentGeneration || accountKey.value != account) return false
        // A cold start's library load takes seconds, and the device holding the session moves on
        // meanwhile: a heartbeat, an auto-advance, a skip.
        val start = adoptionStartFor(
            requested = session,
            latest = effectiveKnowledge(_knowledge.value, _optimistic.value),
            myDeviceId = myDeviceId,
            chosenPositionMs = positionMs,
            nowMs = clock(),
        ) ?: return false
        val current = start.session

        val rows = library.state.value.songsById
        if (rows.isEmpty()) {
            _notices.tryEmit(ConnectNotice("Couldn’t load your library"))
            return false
        }
        val adoption = resolveAdoption(current.queue, current.queueIndex, current.songId) { rows[it] }
        if (adoption == null) {
            _notices.tryEmit(ConnectNotice("This song isn’t available here"))
            return false
        }

        adoptedSongId = current.songId
        player.playlistMetadata = stationMetadata(current.radioSeedId)
        player.setMediaItems(adoption.items.map { it.toMediaItem(api) }, adoption.startIndex, start.positionMs.coerceAtLeast(0))
        player.shuffleModeEnabled = current.shuffle
        player.prepare()
        // Set either way: a paused adoption must not inherit a play-when-ready left over from the
        // queue it replaced.
        player.playWhenReady = play
        _adoptions.tryEmit(Unit)
        requestReport(ReportRequest(claims = if (play) 1 else 0, inResponseTo = inResponseTo))
        return true
    }

    private suspend fun awaitPlayer(): Player? =
        player ?: withTimeoutOrNull(PLAYER_WAIT_MS) { _player.filterNotNull().first() }

    // ---- Local intents and the player's own changes -------------------------------------------

    /**
     * A play, next or previous from the media session while the UI shows another device's session,
     * or a remembered one: the player's own content is hidden leftovers of a queue that moved on,
     * so the session is picked up here instead — the web's media keys (`mediaKey` → `playHere`).
     *
     * With the stream closed — a phone that stopped playing closes it, and a headset's Play can
     * come an hour later — what the UI would show is only what the stream said before it closed:
     * another device may have taken the session since, or played on for an hour. So the server is
     * asked first ([pickUpChecked]), and the answer decides; [local] is the action on this player
     * after all, for when the music turns out to be here.
     */
    private fun pickUpSessionInstead(action: HereAction, local: () -> Unit): Boolean {
        // A share queue is never the account's session, and with the feature off there is none.
        if (!enabled.value || player?.isShareItem == true) return false
        if (!_connected.value) {
            // A key pressed while the question is out joins it, in order, instead of overtaking it.
            pendingKeys?.takeIf { it.generation == localIntentGeneration }?.let { pending ->
                pending.keys += action to local
                return true
            }
            val pending = PendingKeys(++localIntentGeneration).also { it.keys += action to local }
            pendingKeys = pending
            scope.launch { pickUpChecked(pending) }
            return true
        }
        val current = view.value
        if (current.mode == PlaybackMode.Local || current.session == null) return false
        localIntentGeneration++
        playHere(action)
        return true
    }

    /**
     * [pickUpSessionInstead], once the server has said where the session is: picked up here when it
     * is another device's (or remembered), acted on this player when it is this phone's (or there is
     * none). Without an answer, only this player is trusted: a session elsewhere is not picked up
     * from a copy that may be hours old, since its claim would make that copy the account's session.
     *
     * Every key pressed while it was asked is carried out: on this player one after the other, or,
     * for a pick-up, as the steps taken from where the session is (two Nexts land two songs on).
     */
    private suspend fun pickUpChecked(pending: PendingKeys) {
        val answered = checkWithServer()
        if (pendingKeys === pending) pendingKeys = null
        // A pause, a tap or another account meanwhile: that one decides.
        if (pending.generation != localIntentGeneration) return
        val current = viewNow()
        val session = current.session
        when {
            current.mode == PlaybackMode.Local || session == null -> pending.keys.forEach { (_, local) -> local() }
            answered -> adoptHere(
                session,
                pending.keys.first().first,
                knowledgeChecked = true,
                then = pending.keys.drop(1).map { it.first },
            )
            else -> _notices.tryEmit(ConnectNotice("Couldn’t reach the server"))
        }
    }

    /** A controller is about to replace the queue; see [leftForShare]. */
    private fun noteLeavingSession() {
        leftForShare = null
        val player = player ?: return
        val session = _knowledge.value.session ?: return
        if (!enabled.value || !player.holdsLibraryQueue() || session.activeDeviceId != myDeviceId) return
        leftForShare = buildReport(ReportRequest())?.copy(
            isPlaying = false,
            // A share play clears the station before it loads; the session's own seed still holds.
            radioSeedId = session.radioSeedId,
        )
    }

    private fun onLocalIntent(play: Boolean) {
        localIntentGeneration++
        val player = player ?: return
        if (!enabled.value || player.isShareItem) return
        // A seek or a skip on a paused player is not "start playing here"; it only updates a
        // session this device already holds.
        val claims = if (play || player.playWhenReady) 1 else 0
        requestReport(ReportRequest(claims = claims))
    }

    private val listener = object : Player.Listener {
        override fun onEvents(player: Player, events: Player.Events) {
            refreshLocal(player)
            if (events.contains(Player.EVENT_MEDIA_ITEM_TRANSITION)) {
                val current = player.currentMediaItem?.mediaId?.toIntOrNull()
                if (current != adoptedSongId) adoptedSongId = null
            }
            if (events.containsAny(*REPORTED_EVENTS)) requestReport(ReportRequest())
        }

        override fun onTimelineChanged(timeline: androidx.media3.common.Timeline, reason: Int) {
            // A queue edit, not the stream having told the player how long the song is.
            if (reason == Player.TIMELINE_CHANGE_REASON_PLAYLIST_CHANGED) requestReport(ReportRequest())
        }
    }

    private fun refreshLocal(player: Player) {
        val loaded = player.mediaItemCount > 0
        _local.value = LocalStatus(
            attached = true,
            loaded = loaded,
            shareQueue = loaded && player.isShareItem,
            playing = loaded && player.reportsPlaying(),
        )
    }

    // ---- Reports ------------------------------------------------------------------------------

    private fun requestReport(request: ReportRequest) {
        if (!enabled.value) return
        if (request.claims > 0) _claims.update { it + request.claims }
        reports.trySend(request)
    }

    /**
     * One report at a time, each coalescing whatever else was asked for meanwhile: a tap sets the
     * queue, prepares and plays in one go, and that is one report, not five. Two acknowledgements
     * for different commands are never merged — each sender is waiting for its own id.
     */
    private suspend fun runReportLoop() {
        var carried: ReportRequest? = null
        while (true) {
            var merged = carried ?: reports.receive()
            carried = null
            delay(REPORT_COALESCE_MS)
            while (true) {
                val next = reports.tryReceive().getOrNull() ?: break
                if (merged.inResponseTo != null && next.inResponseTo != null && merged.inResponseTo != next.inResponseTo) {
                    carried = next
                    break
                }
                merged = merged.mergedWith(next)
            }
            try {
                sendReport(merged)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                // One failed report must not end the loop that sends all the others.
            } finally {
                _claims.update { (it - merged.claims).coerceAtLeast(0) }
                // The claim is answered: ask the yield question again against what is known now.
                if (merged.claims > 0 && _claims.value == 0) afterKnowledgeChanged()
            }
        }
    }

    private suspend fun sendReport(request: ReportRequest) {
        if (!enabled.value) return
        var report = buildReport(request) ?: return
        if (!request.heartbeat && !request.verify && isRedundantReport(lastSent, lastSentAtMs, report, clock())) return
        val account = accountKey.value
        var attempt = 0
        var resentQueue = false
        while (true) {
            try {
                val response = api.reportPlaybackState(report)
                // Signed into another account while this was in flight: the answer is not ours.
                if (accountKey.value != account) return
                lastSent = report
                lastSentAtMs = clock()
                onReportAnswered(request, response)
                return
            } catch (e: CancellationException) {
                throw e
            } catch (e: ApiException) {
                when (e.status) {
                    403 -> return refuse(SyncSupport.Forbidden)
                    404 -> return refuse(SyncSupport.Unsupported)
                    // "Unchanged" named a queue the server no longer has (it restarted and lost
                    // it): send it in full, once.
                    400 -> if (report.queue == null && !resentQueue) {
                        resentQueue = true
                        report = buildReport(request, fullQueue = true) ?: return
                        continue
                    } else {
                        return
                    }
                }
            } catch (e: NotPairedException) {
                return
            } catch (e: IOException) {
                // Worth chasing below, if it matters.
            }
            // Only a claim, an acknowledgement or a stop is worth chasing: the next change or
            // heartbeat carries everything else anyway (and a stop has no heartbeat to follow it).
            if ((request.claims == 0 && request.inResponseTo == null && !request.stopped) || attempt >= REPORT_RETRIES) return
            delay(reconnectDelayMs(attempt++))
            // Rebuilt, not resent: the player has moved on while we waited.
            report = buildReport(request) ?: return
        }
    }

    private fun onReportAnswered(request: ReportRequest, response: PlaybackStateResponse) {
        _support.value = SyncSupport.Supported
        response.session?.let(::applySession)
        if (response.accepted) {
            snapshotUnconfirmed = false
            return
        }
        // Another device holds the session — the fallback for a device that missed the event that
        // said so (it was asleep). Stop, unless a newer tap here is already on its way to claim it
        // back.
        if (_claims.value - request.claims <= 0) yieldTo(response.session ?: _knowledge.value.session)
    }

    /**
     * The report for the player as it is now, or null when there is nothing to say: no song, a
     * share queue (beyond the one [leftForShare] report, or a stop), or a plain update from a device
     * that does not hold the session.
     */
    private fun buildReport(request: ReportRequest, fullQueue: Boolean = false): PlaybackStateReport? {
        val player = player ?: return reportFromKnowledge(request, fullQueue)
        if (player.isShareItem) {
            // Never the share itself, and no acknowledgement (commands leave a share alone): only
            // the session it left, once, or that the session it is not playing stopped.
            return leftForShare?.also { leftForShare = null }
                ?: reportFromKnowledge(request.copy(inResponseTo = null), fullQueue)
        }
        leftForShare = null
        val songId = player.currentMediaItem?.mediaId?.toIntOrNull() ?: return reportFromKnowledge(request, fullQueue)
        val playing = player.reportsPlaying()
        val claim = request.claims > 0 && playing
        val session = _knowledge.value.session
        val holds = session?.activeDeviceId == myDeviceId
        if (!claim && !holds && !request.verify && request.inResponseTo == null) return null

        val ids = ArrayList<Int>(player.mediaItemCount)
        var index = 0
        for (i in 0 until player.mediaItemCount) {
            val id = player.getMediaItemAt(i).mediaId.toIntOrNull() ?: continue
            if (i == player.currentMediaItemIndex) index = ids.size
            ids += id
        }
        val trimmed = trimQueue(ids, index)
        val metadata = player.mediaMetadata
        return PlaybackStateReport(
            deviceId = myDeviceId,
            installId = identity.installId,
            deviceName = identity.name,
            deviceKind = identity.kind,
            client = identity.client,
            claim = claim,
            inResponseTo = request.inResponseTo,
            songId = songId,
            title = metadata.title?.toString()?.take(DISPLAY_HINT_CAP),
            artist = metadata.artist?.toString()?.take(DISPLAY_HINT_CAP),
            album = metadata.albumTitle?.toString()?.take(DISPLAY_HINT_CAP),
            // Required on a claim; otherwise sent only when it differs from the server's copy.
            queue = if (claim || fullQueue || trimmed.queue != session?.queue) trimmed.queue else null,
            queueIndex = trimmed.queueIndex,
            positionMs = player.currentPosition.coerceAtLeast(0),
            durationMs = player.duration.takeIf { it > 0 } ?: metadata.durationMs?.takeIf { it > 0 },
            isPlaying = playing,
            playbackRate = player.playbackParameters.speed.toDouble(),
            radioSeedId = player.stationSeedId,
            shuffle = player.shuffleModeEnabled,
        )
    }

    /**
     * The session as the server has it, paused where it has got to: the truthful answer from a
     * device holding nothing of it. It acknowledges a command (a pause sent to a phone whose process
     * was restarted since), or says the session stopped ([ReportRequest.stopped]) — as long as the
     * session still says this device plays it, since it may have moved on while this waited.
     */
    private fun reportFromKnowledge(request: ReportRequest, fullQueue: Boolean): PlaybackStateReport? {
        val knowledge = _knowledge.value
        val session = knowledge.session ?: return null
        val stopped = request.stopped &&
            snapshotReportFor(session, myDeviceId, holdsLibraryQueue = false) == SnapshotReport.Stopped
        if (request.inResponseTo == null && !stopped) return null
        return PlaybackStateReport(
            deviceId = myDeviceId,
            installId = identity.installId,
            deviceName = identity.name,
            deviceKind = identity.kind,
            client = identity.client,
            claim = false,
            inResponseTo = request.inResponseTo,
            songId = session.songId,
            title = session.title,
            artist = session.artist,
            album = session.album,
            queue = if (fullQueue) session.queue else null,
            queueIndex = session.queueIndex,
            positionMs = knowledge.positionAt(clock()),
            durationMs = session.durationMs,
            isPlaying = false,
            playbackRate = session.playbackRate,
            radioSeedId = session.radioSeedId,
            shuffle = session.shuffle,
        )
    }

    private companion object {
        /** The legacy wire role word `/api/auth/me` sends for the shared demo account. */
        const val DEMO_ROLE = "Demo"

        const val ERROR_NOT_ACTIVE_DEVICE = "not_active_device"
        const val ERROR_NO_SESSION = "no_session"

        const val REPORT_COALESCE_MS = 150L
        const val REPORT_RETRIES = 3
        const val HEARTBEAT_MS = 20_000L
        const val COMMAND_TIMEOUT_MS = 5_000L
        const val PLAYER_WAIT_MS = 5_000L
        const val STREAM_CLOSE_GRACE_MS = 5_000L
        const val PAUSED_LINGER_MS = 10 * 60_000L
        const val UNSUPPORTED_RETRY_MS = 15 * 60_000L
        const val TRANSFER_MEMORY_MS = 15_000L

        /** How long a media key or an adoption waits for the server to say where the session is. */
        const val SERVER_CHECK_MS = 3_000L

        /** The server cuts title/artist/album to this too; cutting here keeps the body small. */
        const val DISPLAY_HINT_CAP = 512

        /** What changes the session: play/pause, the song, a seek, the rate, shuffle, a stall's end. */
        val REPORTED_EVENTS = intArrayOf(
            Player.EVENT_PLAY_WHEN_READY_CHANGED,
            Player.EVENT_PLAYBACK_STATE_CHANGED,
            Player.EVENT_PLAYBACK_SUPPRESSION_REASON_CHANGED,
            Player.EVENT_MEDIA_ITEM_TRANSITION,
            Player.EVENT_POSITION_DISCONTINUITY,
            Player.EVENT_PLAYBACK_PARAMETERS_CHANGED,
            Player.EVENT_SHUFFLE_MODE_ENABLED_CHANGED,
        )
    }
}

/** What [LocalIntentPlayer] tells playback sync, and asks it, on the way through. */
internal interface LocalIntents {
    /** A local intent reached the player: [play] for a play, false for a skip or a seek. */
    fun onIntent(play: Boolean)

    /** A local pause reached the player: anything still waiting to start the music gives way. */
    fun onPause() {}

    /**
     * Whether [action] picks the account's session up here instead of acting on the player — true
     * while the player's own content is hidden (another device's session is shown, or a remembered
     * one), and while that has to be asked of the server first. The player then leaves it alone;
     * [local] carries the action out on it after all, if the answer is that the music is here.
     */
    fun pickUpInstead(action: PlaybackConnect.HereAction, local: () -> Unit): Boolean

    /** A controller is about to replace the queue. */
    fun beforeQueueReplaced()
}

/**
 * The session player the media session drives: the service's ExoPlayer, with every local play
 * intent reported on the way through.
 *
 * A play, next or previous first asks [LocalIntents.pickUpInstead]: from the notification, the lock
 * screen or a headset, while another device holds the session, it means the account's music, not
 * the leftovers of a queue that moved on. The exception is a play right after a controller loaded a
 * queue of its own — a row tap is a set-then-play — which is about that queue, and claims it. A
 * pick-up that first has to ask the server may hand the call back, to be made on this player after
 * all.
 */
internal class LocalIntentPlayer(player: Player, private val intents: LocalIntents) : ForwardingPlayer(player) {
    /** A controller loaded a queue since the last play, so the next play is about that queue. */
    private var queueLoaded = false

    /** Carrying out an action a pick-up handed back ([LocalIntents.pickUpInstead]'s `local`). */
    private var handedBack = false

    override fun play() {
        if (pickedUp(PlaybackConnect.HereAction.Resume, ::play)) return
        super.play()
        played()
    }

    override fun setPlayWhenReady(playWhenReady: Boolean) {
        if (playWhenReady && pickedUp(PlaybackConnect.HereAction.Resume) { setPlayWhenReady(true) }) return
        if (!playWhenReady) intents.onPause()
        super.setPlayWhenReady(playWhenReady)
        if (playWhenReady) played()
    }

    // A pause from the app, the notification or a headset. Sync's own pauses (a yield, a remote
    // Pause) go to the raw player, so they never land here.
    override fun pause() {
        intents.onPause()
        super.pause()
    }

    override fun seekToNext() {
        if (pickedUp(PlaybackConnect.HereAction.Next, ::seekToNext)) return
        super.seekToNext()
        intents.onIntent(false)
    }

    override fun seekToNextMediaItem() {
        if (pickedUp(PlaybackConnect.HereAction.Next, ::seekToNextMediaItem)) return
        super.seekToNextMediaItem()
        intents.onIntent(false)
    }

    override fun seekToPrevious() {
        if (pickedUp(PlaybackConnect.HereAction.Previous, ::seekToPrevious)) return
        super.seekToPrevious()
        intents.onIntent(false)
    }

    override fun seekToPreviousMediaItem() {
        if (pickedUp(PlaybackConnect.HereAction.Previous, ::seekToPreviousMediaItem)) return
        super.seekToPreviousMediaItem()
        intents.onIntent(false)
    }

    // A seek moves this player's own position — the leftovers', while they are hidden, which is
    // harmless (the web ignores a lock-screen scrub then) — and is never a pick-up.

    override fun seekTo(positionMs: Long) {
        super.seekTo(positionMs)
        intents.onIntent(false)
    }

    override fun seekTo(mediaItemIndex: Int, positionMs: Long) {
        super.seekTo(mediaItemIndex, positionMs)
        intents.onIntent(false)
    }

    override fun seekToDefaultPosition() {
        super.seekToDefaultPosition()
        intents.onIntent(false)
    }

    override fun seekToDefaultPosition(mediaItemIndex: Int) {
        super.seekToDefaultPosition(mediaItemIndex)
        intents.onIntent(false)
    }

    override fun setMediaItems(mediaItems: MutableList<MediaItem>) = loading { super.setMediaItems(mediaItems) }

    override fun setMediaItems(mediaItems: MutableList<MediaItem>, resetPosition: Boolean) =
        loading { super.setMediaItems(mediaItems, resetPosition) }

    override fun setMediaItems(mediaItems: MutableList<MediaItem>, startIndex: Int, startPositionMs: Long) =
        loading { super.setMediaItems(mediaItems, startIndex, startPositionMs) }

    override fun setMediaItem(mediaItem: MediaItem) = loading { super.setMediaItem(mediaItem) }

    override fun setMediaItem(mediaItem: MediaItem, startPositionMs: Long) =
        loading { super.setMediaItem(mediaItem, startPositionMs) }

    override fun setMediaItem(mediaItem: MediaItem, resetPosition: Boolean) =
        loading { super.setMediaItem(mediaItem, resetPosition) }

    private inline fun loading(replace: () -> Unit) {
        intents.beforeQueueReplaced()
        replace()
        queueLoaded = true
    }

    /** [again] is the same call, made once more — past this question — if the pick-up hands it back. */
    private fun pickedUp(action: PlaybackConnect.HereAction, again: () -> Unit): Boolean =
        !handedBack && !queueLoaded && intents.pickUpInstead(action) {
            handedBack = true
            try {
                again()
            } finally {
                handedBack = false
            }
        }

    private fun played() {
        queueLoaded = false
        intents.onIntent(true)
    }
}

/**
 * Set to play the account's session here: a library queue, not a share link's. Only this can be
 * superseded — a share playing here is not the session, so the session moving elsewhere leaves it be.
 */
internal fun Player.playsSessionHere(): Boolean = playWhenReady && holdsLibraryQueue()

/** Something loaded, and not a share link's queue. */
private fun Player.holdsLibraryQueue(): Boolean = mediaItemCount > 0 && !isShareItem

/**
 * What the session calls playing: set to play and ready or buffering towards it. A stall is not a
 * pause; a phone call holding the audio focus is.
 */
private fun Player.reportsPlaying(): Boolean =
    playWhenReady &&
        (playbackState == Player.STATE_READY || playbackState == Player.STATE_BUFFERING) &&
        playbackSuppressionReason == Player.PLAYBACK_SUPPRESSION_REASON_NONE

/** Carries on from where it is — `PlayerController.resume`'s rule, on the service's player. */
private fun Player.resumePlayback() {
    when (playbackState) {
        Player.STATE_IDLE -> prepare()
        Player.STATE_ENDED -> seekTo(currentMediaItemIndex, 0L)
    }
    play()
}
