package com.musichoarder.app.player

import android.os.Looper
import androidx.annotation.OptIn
import androidx.media3.common.MediaItem
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.test.utils.FakeClock
import androidx.media3.test.utils.FakeMediaSourceFactory
import androidx.media3.test.utils.FakeTimeline
import androidx.media3.test.utils.TestExoPlayerBuilder
import com.musichoarder.app.data.AlbumsResponse
import com.musichoarder.app.data.ApiSong
import com.musichoarder.app.data.DeviceIdentity
import com.musichoarder.app.data.DeviceKind
import com.musichoarder.app.data.LibraryRepository
import com.musichoarder.app.data.MusicHoarderApi
import com.musichoarder.app.data.PlaybackCommandAccepted
import com.musichoarder.app.data.PlaybackCommandBody
import com.musichoarder.app.data.PlaybackDevice
import com.musichoarder.app.data.PlaybackEvent
import com.musichoarder.app.data.PlaybackJson
import com.musichoarder.app.data.PlaybackMode
import com.musichoarder.app.data.PlaybackSessionDto
import com.musichoarder.app.data.PlaybackSessionEvent
import com.musichoarder.app.data.PlaybackSnapshot
import com.musichoarder.app.data.PlaybackStateReport
import com.musichoarder.app.data.PlaybackStateResponse
import com.musichoarder.app.data.SessionStore
import com.musichoarder.app.data.SongsResponse
import com.musichoarder.app.data.StoredAccount
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import okhttp3.Call
import okhttp3.Interceptor
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Protocol
import okhttp3.Request
import okhttp3.Response
import okhttp3.ResponseBody.Companion.asResponseBody
import okhttp3.ResponseBody.Companion.toResponseBody
import okio.Buffer
import okio.Source
import okio.Timeout
import okio.buffer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import java.io.IOException
import java.time.Duration
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

/**
 * [PlaybackConnect] end to end, against a fake server behind the real HTTP client: its event
 * stream, its snapshot, its reports and its commands. The player is a real ExoPlayer (on a clock
 * that never moves, so a song stays where it was put), and the media session's side of it is
 * [PlaybackConnect.sessionPlayer] — what the notification, the lock screen and a headset drive.
 *
 * What it pins is the part the pure rules in `PlaybackSync.kt` cannot: which copy of the session a
 * phone acts on. A phone that stopped playing closes its stream, and a headset's Play can come an
 * hour later; by then the session may have moved to another device, or played on there. Acting on
 * the copy the stream left behind would claim it — making an hour-old queue the account's session.
 */
@OptIn(UnstableApi::class)
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34])
class PlaybackConnectTest {

    private val app = RuntimeEnvironment.getApplication()
    private val mainLooper = shadowOf(Looper.getMainLooper())
    private val server = FakeServer()
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val notices = CopyOnWriteArrayList<ConnectNotice>()

    private lateinit var connect: PlaybackConnect
    private lateinit var exo: ExoPlayer

    /** What the media session drives: the notification, the lock screen, a headset. */
    private lateinit var media: Player
    private lateinit var me: String

    @Before
    fun setUp() {
        val sessions = SessionStore(app)
        runBlocking { sessions.addAccount(StoredAccount(BASE_URL, "token", role = "Owner")) }
        val api = MusicHoarderApi(OkHttpClient.Builder().addInterceptor(server).build(), sessions)
        val identity = DeviceIdentity(app)
        me = identity.deviceId
        connect = PlaybackConnect(api, sessions, LibraryRepository(api), identity)
        exo = TestExoPlayerBuilder(app)
            .setClock(FakeClock(/* isAutoAdvancing = */ false))
            .setMediaSourceFactory(
                FakeMediaSourceFactory(FakeTimeline.TimelineWindowDefinition.Builder().setDurationUs(DURATION_MS * 1_000)),
            )
            .build()
        connect.attach(exo)
        media = connect.sessionPlayer(exo)
        scope.launch { connect.notices.collect { notices += it } }
    }

    @After
    fun tearDown() {
        server.shutdown()
        scope.cancel()
        connect.setUiStarted(false)
        connect.detach(exo)
        exo.release()
    }

    // ---- The scenarios ---------------------------------------------------------------------------

    @Test
    fun `a headset Play after the stream closed picks the session up where it is now`() {
        server.session = session(MAC, version = 1, songId = 1, queueIndex = 0, positionMs = 0)
        openStream()
        assertEquals(PlaybackMode.Remote, connect.view.value.mode)

        // The app goes to the background with nothing playing here: the stream closes.
        closeStream()
        // The Mac plays on, and the phone does not hear of it.
        server.session = session(MAC, version = 2, songId = 6, queueIndex = 5, positionMs = 70_000)
        advance(Duration.ofMinutes(20))

        media.play()

        val claim = awaitClaim()
        assertEquals(6, claim.songId)
        assertEquals(5, claim.queueIndex)
        assertEquals(QUEUE, claim.queue)
        assertTrue("picked up at the Mac's position, not the cached copy's end: ${claim.positionMs}", claim.positionMs in 70_000L..75_000L)
        assertEquals("6", exo.currentMediaItem?.mediaId)
        assertTrue("never claims the copy the stream left behind", server.reports.none { it.songId == 1 || it.songId == 2 })
    }

    @Test
    fun `a headset Play picks up what another device took over while the stream was closed`() {
        openStream()
        playOwnQueue(listOf(1, 2, 3))
        media.pause()
        runUntil("the pause is reported") { server.reports.lastOrNull()?.isPlaying == false }

        // Out of the app, paused: the stream stays open for ten minutes, then closes.
        connect.setUiStarted(false)
        advance(Duration.ofMinutes(10))
        closeStream(uiStopped = true)
        // The Mac takes the session over; the phone never hears of it.
        server.session = session(MAC, version = server.session!!.version + 1, songId = 8, queue = listOf(7, 8, 9), queueIndex = 1, positionMs = 30_000)
        advance(Duration.ofMinutes(2))
        server.reports.clear()

        media.play()

        val claim = awaitClaim()
        assertEquals(8, claim.songId)
        assertEquals(listOf(7, 8, 9), claim.queue)
        assertEquals("8", exo.currentMediaItem?.mediaId)
        assertTrue("never resumes its own old queue", server.reports.none { it.songId == 1 })
    }

    @Test
    fun `a headset Play with the stream closed resumes this phone's own music when it is still here`() {
        openStream()
        playOwnQueue(listOf(1, 2, 3))
        media.pause()
        runUntil("the pause is reported") { server.reports.lastOrNull()?.isPlaying == false }
        connect.setUiStarted(false)
        advance(Duration.ofMinutes(10))
        closeStream(uiStopped = true)
        server.reports.clear()
        val fetchesBefore = server.fetches.get()

        media.play()

        val claim = awaitClaim()
        assertEquals(1, claim.songId)
        assertEquals(listOf(1, 2, 3), claim.queue)
        assertTrue(exo.playWhenReady)
        assertEquals("asked the server first", fetchesBefore + 1, server.fetches.get())
    }

    @Test
    fun `Play here with the stream closed picks the session up where it is now`() {
        // The app's own Play here, in the moment before its stream is back.
        server.session = session(MAC, version = 1, songId = 1, queueIndex = 0, positionMs = 0)
        openStream()
        closeStream()
        server.session = session(MAC, version = 2, songId = 4, queueIndex = 3, positionMs = 12_000)
        advance(Duration.ofMinutes(20))

        connect.playHere()

        val claim = awaitClaim()
        assertEquals(4, claim.songId)
        assertTrue("at the Mac's position: ${claim.positionMs}", claim.positionMs in 12_000L..17_000L)
    }

    @Test
    fun `a snapshot fetched with the stream closed is not the stream, so the next key asks again`() {
        // A cold start by a headset key: no UI, no stream, nothing loaded, no session anywhere.
        media.play()
        runUntil("the check") { server.fetches.get() == 1 }
        runFor(500)
        assertEquals(false, connect.view.value.connected)
        assertEquals(0, server.openStreams.get())

        media.play()
        runUntil("the second check") { server.fetches.get() == 2 }
    }

    @Test
    fun `a transfer to the remembered holder that never picks up says it could not be reached`() {
        // The Pixel held the session, was killed, and came back with nothing loaded: it is listed
        // again, and the session still names it.
        server.session = session(PIXEL, version = 1, songId = 1, queueIndex = 0, isPlaying = false, live = false)
        server.devices = listOf(device(PIXEL, "Pixel"))
        openStream()
        assertEquals(PlaybackMode.Remembered, connect.view.value.mode)

        connect.transferTo(PIXEL)
        runUntil("the command is sent") { server.commands.isNotEmpty() }
        runFor(COMMAND_TIMEOUT_MS + 500)

        val notice = notices.singleOrNull()
        assertEquals("Couldn’t reach Pixel", notice?.message)
        assertTrue(notice!!.offerPlayHere)
    }

    @Test
    fun `a transfer to the remembered holder lands once its claim acknowledges it`() {
        server.session = session(PIXEL, version = 1, songId = 1, queueIndex = 0, isPlaying = false, live = false)
        server.devices = listOf(device(PIXEL, "Pixel"))
        openStream()

        connect.transferTo(PIXEL)
        runUntil("the command is sent") { server.commands.isNotEmpty() }
        runFor(500)
        // The Pixel picks it up and claims it, answering the command.
        server.push(
            PlaybackEvent.SESSION,
            PlaybackJson.encodeToString(
                PlaybackSessionEvent.serializer(),
                PlaybackSessionEvent(session(PIXEL, version = 2, songId = 1, queueIndex = 0, lastCommandId = server.commands.single())),
            ),
        )
        runFor(COMMAND_TIMEOUT_MS + 500)

        assertEquals(emptyList<ConnectNotice>(), notices.toList())
    }

    @Test
    fun `keys pressed while the server is asked are all carried out, in order`() {
        pausedInPocket(listOf(1, 2, 3, 4))
        val fetchesBefore = server.fetches.get()
        holdChecks()

        // Two Nexts and a Play on the lock screen, on a slow radio.
        media.seekToNext()
        media.seekToNext()
        media.play()
        answerChecks()

        val claim = awaitClaim()
        assertEquals(3, claim.songId)
        assertEquals("3", exo.currentMediaItem?.mediaId)
        assertTrue(exo.playWhenReady)
        assertEquals("one question for all three", fetchesBefore + 1, server.fetches.get())
    }

    @Test
    fun `keys pressed while the server is asked are steps of the one pick-up`() {
        pausedInPocket(listOf(1, 2, 3))
        // The Mac takes the session over; the phone never hears of it.
        server.session = session(MAC, version = server.session!!.version + 1, songId = 6, queueIndex = 5, positionMs = 30_000)
        holdChecks()

        media.seekToNext()
        media.seekToNext()
        answerChecks()

        val claim = awaitClaim()
        assertEquals(8, claim.songId)
        assertEquals(QUEUE, claim.queue)
        assertEquals("8", exo.currentMediaItem?.mediaId)
    }

    @Test
    fun `a pause while the server is asked cancels the Play before it`() {
        pausedInPocket(listOf(1, 2, 3))
        holdChecks()

        media.play()
        media.pause()
        answerChecks()

        assertFalse("the pause was the last word", exo.playWhenReady)
        assertTrue("nothing claimed", server.reports.none { it.claim })
    }

    @Test
    fun `a pause while the server is asked cancels the pick-up it would have made`() {
        pausedInPocket(listOf(1, 2, 3))
        server.session = session(MAC, version = server.session!!.version + 1, songId = 6, queueIndex = 5, positionMs = 30_000)
        holdChecks()

        media.play()
        media.pause()
        answerChecks()

        assertFalse(exo.playWhenReady)
        assertEquals("the leftovers are left alone", "1", exo.currentMediaItem?.mediaId)
        assertTrue("nothing claimed", server.reports.none { it.claim })
    }

    // ---- Driving it ------------------------------------------------------------------------------

    /**
     * This phone played its own [ids], paused, and was left in a pocket until its stream closed:
     * it still holds the session, as far as it knows.
     */
    private fun pausedInPocket(ids: List<Int>) {
        openStream()
        playOwnQueue(ids)
        media.pause()
        runUntil("the pause is reported") { server.reports.lastOrNull()?.isPlaying == false }
        connect.setUiStarted(false)
        advance(Duration.ofMinutes(10))
        closeStream(uiStopped = true)
        server.reports.clear()
    }

    /** From now on the server takes its time over `GET /api/playback`, until [answerChecks]. */
    private fun holdChecks() {
        server.fetchGate = CountDownLatch(1)
    }

    /** The server answers what it was asked (well within the phone's 3 s), and the answers land. */
    private fun answerChecks() {
        runUntil("the check") { server.fetches.get() > server.answeredFetches.get() }
        runFor(100)
        server.fetchGate?.countDown()
        runUntil("the answers") { server.answeredFetches.get() == server.fetches.get() }
        runFor(500)
    }

    /** The app's UI comes up, and the stream with it. */
    private fun openStream() {
        connect.setUiStarted(true)
        runUntil("the stream's snapshot") { connect.view.value.connected }
    }

    /** The app's UI goes away with nothing holding the stream open: it closes, a moment later. */
    private fun closeStream(uiStopped: Boolean = false) {
        if (!uiStopped) connect.setUiStarted(false)
        runUntil("the stream to close") { !connect.view.value.connected && server.openStreams.get() == 0 }
    }

    /** A row tap: a queue loaded through the session, then played — a claim. */
    private fun playOwnQueue(ids: List<Int>) {
        media.setMediaItems(ids.map(::item))
        media.prepare()
        media.play()
        runUntil("the claim is accepted") { server.session?.activeDeviceId == me && connect.view.value.mode == PlaybackMode.Local }
    }

    private fun awaitClaim(): PlaybackStateReport {
        runUntil("a claim") { server.reports.any { it.claim } }
        return server.reports.first { it.claim }
    }

    private fun item(id: Int): MediaItem =
        MediaItem.Builder().setMediaId(id.toString()).setUri("$BASE_URL/api/mh/songs/$id/stream").build()

    /** Moves the main thread's clock on in one go, then lets any answers on their way land. */
    private fun advance(duration: Duration) {
        mainLooper.idleFor(duration)
        runFor(50)
    }

    /**
     * Runs the main thread in small steps of its clock, giving the network threads real time to
     * answer between them, until [done]. The steps keep the clock roughly in line with the network,
     * so no timeout here fires merely because an answer took a few real milliseconds.
     */
    private fun runUntil(what: String, done: () -> Boolean) {
        val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(20)
        while (!done()) {
            check(System.nanoTime() < deadline) { "Timed out waiting for $what" }
            step()
        }
    }

    private fun runFor(clockMs: Long) {
        repeat((clockMs / STEP_MS).toInt()) { step() }
    }

    private fun step() {
        mainLooper.idleFor(Duration.ofMillis(STEP_MS))
        Thread.sleep(1)
        mainLooper.idle()
    }

    // ---- The fake server -------------------------------------------------------------------------

    private fun session(
        device: String,
        version: Long,
        songId: Int,
        queue: List<Int> = QUEUE,
        queueIndex: Int,
        positionMs: Long = 0,
        isPlaying: Boolean = true,
        live: Boolean = true,
        lastCommandId: String? = null,
    ) = PlaybackSessionDto(
        version = version,
        songId = songId,
        title = "Song $songId",
        queue = queue,
        queueIndex = queueIndex,
        positionMs = positionMs,
        durationMs = DURATION_MS,
        isPlaying = isPlaying,
        activeDeviceId = device,
        activeDeviceName = if (device == MAC) "MacBook" else "Pixel",
        live = live,
        lastCommandId = lastCommandId,
    )

    private fun device(id: String, name: String) =
        PlaybackDevice(deviceId = id, name = name, kind = DeviceKind.PHONE, client = "android", online = true)

    /**
     * The API behind the frontend's proxy, as far as playback sync and the library dump go. Holds
     * one session, and applies reports the way the server does: a claim always takes it, and
     * anything else only from the device holding it.
     */
    private class FakeServer : Interceptor {
        @Volatile var session: PlaybackSessionDto? = null
        @Volatile var devices: List<PlaybackDevice> = emptyList()
        val reports = CopyOnWriteArrayList<PlaybackStateReport>()
        val commands = CopyOnWriteArrayList<String>()
        val fetches = AtomicInteger()
        val answeredFetches = AtomicInteger()

        /** While closed, `GET /api/playback` waits for it: a slow radio. */
        @Volatile var fetchGate: CountDownLatch? = null
        val openStreams = AtomicInteger()
        private val streams = CopyOnWriteArrayList<EventStream>()
        private val commandIds = AtomicInteger()

        @Volatile private var shutDown = false

        /** An event on every open stream. */
        fun push(event: String, data: String) {
            streams.forEach { it.events.put("event: $event\ndata: $data\n\n") }
        }

        fun shutdown() {
            shutDown = true
            fetchGate?.countDown()
        }

        override fun intercept(chain: Interceptor.Chain): Response {
            val request = chain.request()
            val path = request.url.encodedPath.removePrefix("/api/mh")
            return when {
                path == "/api/playback/stream" -> stream(request, chain.call())
                path == "/api/playback" -> {
                    fetches.incrementAndGet()
                    fetchGate?.await(20, TimeUnit.SECONDS)
                    json(request, PlaybackJson.encodeToString(PlaybackSnapshot.serializer(), snapshot()))
                        .also { answeredFetches.incrementAndGet() }
                }
                path == "/api/playback/state" -> {
                    val report = PlaybackJson.decodeFromString(PlaybackStateReport.serializer(), request.bodyText())
                    json(request, PlaybackJson.encodeToString(PlaybackStateResponse.serializer(), report(report)))
                }
                path == "/api/playback/command" -> {
                    PlaybackJson.decodeFromString(PlaybackCommandBody.serializer(), request.bodyText())
                    val id = "cmd-${commandIds.incrementAndGet()}"
                    commands += id
                    json(request, PlaybackJson.encodeToString(PlaybackCommandAccepted.serializer(), PlaybackCommandAccepted(id)), code = 202)
                }
                path == "/songs" -> json(
                    request,
                    PlaybackJson.encodeToString(SongsResponse.serializer(), SongsResponse(songs = SONGS.map(::song))),
                )
                path == "/api/albums" -> json(request, PlaybackJson.encodeToString(AlbumsResponse.serializer(), AlbumsResponse()))
                else -> json(request, "{}", code = 404)
            }
        }

        private fun snapshot() = PlaybackSnapshot(session, devices)

        @Synchronized
        private fun report(report: PlaybackStateReport): PlaybackStateResponse {
            reports += report
            val current = session
            val accepted = report.claim || current?.activeDeviceId == report.deviceId
            if (accepted) {
                session = PlaybackSessionDto(
                    version = (current?.version ?: 0) + 1,
                    songId = report.songId,
                    title = report.title,
                    queue = report.queue ?: current?.queue.orEmpty(),
                    queueIndex = report.queueIndex,
                    positionMs = report.positionMs,
                    durationMs = report.durationMs,
                    isPlaying = report.isPlaying,
                    playbackRate = report.playbackRate,
                    radioSeedId = report.radioSeedId,
                    shuffle = report.shuffle,
                    activeDeviceId = report.deviceId,
                    activeDeviceName = report.deviceName,
                    live = true,
                    lastCommandId = report.inResponseTo ?: current?.lastCommandId,
                )
            }
            return PlaybackStateResponse(accepted, session)
        }

        private fun stream(request: Request, call: Call): Response {
            val stream = EventStream(call)
            stream.events.put("event: ${PlaybackEvent.SNAPSHOT}\ndata: ${PlaybackJson.encodeToString(PlaybackSnapshot.serializer(), snapshot())}\n\n")
            streams += stream
            openStreams.incrementAndGet()
            return Response.Builder()
                .request(request)
                .protocol(Protocol.HTTP_1_1)
                .code(200)
                .message("OK")
                .body(stream.buffer().asResponseBody("text/event-stream".toMediaType()))
                .build()
        }

        /** Open until the phone hangs up (or the test ends), like the real one between its pings. */
        private inner class EventStream(private val call: Call) : Source {
            val events = LinkedBlockingQueue<String>()
            private val pending = Buffer()
            private var open = true

            override fun read(sink: Buffer, byteCount: Long): Long {
                while (pending.size == 0L) {
                    if (shutDown) return end(-1)
                    if (call.isCanceled()) {
                        end(0)
                        throw IOException("Canceled")
                    }
                    val next = events.poll(2, TimeUnit.MILLISECONDS) ?: continue
                    pending.writeUtf8(next)
                }
                return pending.read(sink, byteCount)
            }

            private fun end(result: Long): Long {
                if (open) {
                    open = false
                    streams -= this
                    openStreams.decrementAndGet()
                }
                return result
            }

            override fun timeout(): Timeout = Timeout.NONE

            override fun close() {
                end(0)
            }
        }

        private fun json(request: Request, body: String, code: Int = 200): Response = Response.Builder()
            .request(request)
            .protocol(Protocol.HTTP_1_1)
            .code(code)
            .message(if (code < 300) "OK" else "Not Found")
            .body(body.toResponseBody("application/json".toMediaType()))
            .build()

        private fun Request.bodyText(): String = Buffer().also { body!!.writeTo(it) }.readUtf8()

        private fun song(id: Int) = ApiSong(
            id = id,
            fileName = "$id.flac",
            title = "Song $id",
            artist = "An Artist",
            album = "An Album",
            destinationPath = "/library/An Artist/An Album/$id.flac",
            isBuiltServer = true,
        )
    }

    private companion object {
        const val BASE_URL = "https://music.test"
        const val MAC = "mac-000001"
        const val PIXEL = "pixel-0001"
        const val DURATION_MS = 215_000L
        const val STEP_MS = 5L
        const val COMMAND_TIMEOUT_MS = 5_000L
        val QUEUE = (1..10).toList()
        val SONGS = (1..10).toList()
    }
}
