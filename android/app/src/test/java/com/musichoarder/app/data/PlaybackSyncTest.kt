package com.musichoarder.app.data

import android.content.res.Configuration
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins the playback-sync rules (`PlaybackSync.kt`) case for case against their web twins in
 * `frontend/src/lib/playback-sync/session.ts` (`session.test.ts`): which mode the player is in,
 * where a remote song is by now, how a queue is cut to the wire's cap, when to stop because the
 * music moved, what the picker lists and what the lines say. If one of these changes, the other
 * client has to change with it — the two must agree about where the music is.
 */
class PlaybackSyncTest {

    private fun session(
        version: Long = 1,
        songId: Int = 123,
        queue: List<Int> = listOf(120, 123, 131),
        queueIndex: Int = 1,
        positionMs: Long = 83_000,
        durationMs: Long? = 215_000,
        isPlaying: Boolean = true,
        playbackRate: Double = 1.0,
        activeDeviceId: String? = PHONE,
        activeDeviceName: String? = "Safari on iPhone",
        live: Boolean = true,
        lastCommandId: String? = null,
    ) = PlaybackSessionDto(
        version = version,
        songId = songId,
        title = "Nightswim",
        artist = "R.E.M.",
        queue = queue,
        queueIndex = queueIndex,
        positionMs = positionMs,
        durationMs = durationMs,
        isPlaying = isPlaying,
        playbackRate = playbackRate,
        radioSeedId = 120,
        activeDeviceId = activeDeviceId,
        activeDeviceName = activeDeviceName,
        live = live,
        lastCommandId = lastCommandId,
    )

    private fun device(id: String, name: String, online: Boolean = true, kind: String = DeviceKind.COMPUTER) =
        PlaybackDevice(deviceId = id, name = name, kind = kind, client = "web", online = online)

    // ---- Mode ----------------------------------------------------------------------------------

    @Test
    fun `is local with no session or the feature off`() {
        assertEquals(PlaybackMode.Local, playbackModeFor(null, ME, featureOn = true))
        assertEquals(PlaybackMode.Local, playbackModeFor(session(), ME, featureOn = false))
    }

    @Test
    fun `is local while this device holds it, live or not`() {
        assertEquals(PlaybackMode.Local, playbackModeFor(session(activeDeviceId = ME), ME, true))
        // Its own stream momentarily down: still the one playing.
        assertEquals(PlaybackMode.Local, playbackModeFor(session(activeDeviceId = ME, live = false), ME, true))
    }

    @Test
    fun `is remote while another reachable device holds it`() {
        assertEquals(PlaybackMode.Remote, playbackModeFor(session(), ME, true))
        assertEquals(PlaybackMode.Remote, playbackModeFor(session(isPlaying = false), ME, true))
    }

    @Test
    fun `is remembered when the device holding it is gone, or there is none`() {
        assertEquals(PlaybackMode.Remembered, playbackModeFor(session(live = false), ME, true))
        assertEquals(PlaybackMode.Remembered, playbackModeFor(session(activeDeviceId = null, live = false), ME, true))
    }

    @Test
    fun `a claim on its way shows the local player at once`() {
        val remote = playbackModeFor(session(), ME, true)
        assertEquals(PlaybackMode.Local, displayModeFor(remote, session(), ME, localLoaded = true, claimInFlight = true))
        assertEquals(PlaybackMode.Remote, displayModeFor(remote, session(), ME, localLoaded = true, claimInFlight = false))
    }

    @Test
    fun `a session this device held before its process died shows as remembered`() {
        val held = session(activeDeviceId = ME, isPlaying = false)
        assertEquals(PlaybackMode.Remembered, displayModeFor(PlaybackMode.Local, held, ME, localLoaded = false, claimInFlight = false))
        // Loaded here: it is simply this phone's player.
        assertEquals(PlaybackMode.Local, displayModeFor(PlaybackMode.Local, held, ME, localLoaded = true, claimInFlight = false))
        // No session at all: nothing to remember.
        assertEquals(PlaybackMode.Local, displayModeFor(PlaybackMode.Local, null, ME, localLoaded = false, claimInFlight = false))
    }

    // ---- Position ------------------------------------------------------------------------------

    @Test
    fun `stands still while paused`() {
        assertEquals(83_000L, extrapolatedPositionMs(83_000, 215_000, isPlaying = false, playbackRate = 1.0, elapsedMs = 5_000))
    }

    @Test
    fun `moves with the clock while playing, at the playback rate`() {
        assertEquals(88_000L, extrapolatedPositionMs(83_000, 215_000, true, 1.0, 5_000))
        assertEquals(90_500L, extrapolatedPositionMs(83_000, 215_000, true, 1.5, 5_000))
        // A rate that is not a positive number reads as 1×.
        assertEquals(88_000L, extrapolatedPositionMs(83_000, 215_000, true, 0.0, 5_000))
        assertEquals(88_000L, extrapolatedPositionMs(83_000, 215_000, true, Double.NaN, 5_000))
    }

    @Test
    fun `never runs past the end, nor backwards`() {
        assertEquals(215_000L, extrapolatedPositionMs(214_000, 215_000, true, 1.0, 60_000))
        assertEquals(0L, extrapolatedPositionMs(-20, 215_000, true, 1.0, 0))
        // A clock that stepped back does not rewind the song.
        assertEquals(83_000L, extrapolatedPositionMs(83_000, 215_000, true, 1.0, -5_000))
    }

    @Test
    fun `runs on without a known duration`() {
        assertEquals(1_083_000L, extrapolatedPositionMs(83_000, null, true, 1.0, 1_000_000))
        assertEquals(1_083_000L, extrapolatedPositionMs(83_000, 0, true, 1.0, 1_000_000))
    }

    // ---- Queue cap -----------------------------------------------------------------------------

    private fun range(n: Int) = (0 until n).toList()

    @Test
    fun `sends a queue within the cap as it is`() {
        assertEquals(TrimmedQueue(listOf(1, 2, 3), 2), trimQueue(listOf(1, 2, 3), 2))
        assertEquals(PLAYBACK_QUEUE_CAP, trimQueue(range(PLAYBACK_QUEUE_CAP), 999).queue.size)
    }

    @Test
    fun `keeps the current song and 50 before it, re-basing the index`() {
        val trimmed = trimQueue(range(2000), 700)
        assertEquals(PLAYBACK_QUEUE_CAP, trimmed.queue.size)
        assertEquals(650, trimmed.queue.first())
        assertEquals(1649, trimmed.queue.last())
        assertEquals(50, trimmed.queueIndex)
        assertEquals(700, trimmed.queue[trimmed.queueIndex])
    }

    @Test
    fun `starts at the head when the song is near it`() {
        val trimmed = trimQueue(range(1200), 10)
        assertEquals(0, trimmed.queue.first())
        assertEquals(10, trimmed.queueIndex)
        assertEquals(PLAYBACK_QUEUE_CAP, trimmed.queue.size)
    }

    @Test
    fun `takes what is left near the tail`() {
        val trimmed = trimQueue(range(1500), 1499)
        assertEquals(range(1500).drop(1449), trimmed.queue)
        assertEquals(1499, trimmed.queue[trimmed.queueIndex])
    }

    // ---- Versions ------------------------------------------------------------------------------

    @Test
    fun `always applies a snapshot, even an older one`() {
        val known = SyncKnowledge().withSnapshot(PlaybackSnapshot(session(version = 40)), nowMs = 0)
        // An API restart can send the versions backwards; a snapshot is the truth all the same.
        val restarted = known.withSnapshot(PlaybackSnapshot(session(version = 3), listOf(device(PHONE, "iPhone"))), nowMs = 10)
        assertEquals(3L, restarted.session?.version)
        assertEquals(3L, restarted.appliedVersion)
        assertEquals(10L, restarted.receivedAtMs)
        assertEquals(1, restarted.devices.size)
        // An empty snapshot forgets the session too.
        assertNull(restarted.withSnapshot(PlaybackSnapshot(), nowMs = 20).session)
    }

    @Test
    fun `applies a session event only when it is newer`() {
        val known = SyncKnowledge().withSnapshot(PlaybackSnapshot(session(version = 5)), nowMs = 0)
        assertSame(known, known.withSession(session(version = 5), nowMs = 1))
        assertSame(known, known.withSession(session(version = 4), nowMs = 1))
        val newer = known.withSession(session(version = 6, positionMs = 1_000), nowMs = 2)
        assertEquals(6L, newer.appliedVersion)
        assertEquals(2L, newer.receivedAtMs)
        // The first session event after a snapshot with no session always lands.
        val empty = SyncKnowledge().withSnapshot(PlaybackSnapshot(), nowMs = 0)
        assertEquals(1L, empty.withSession(session(version = 1), nowMs = 1).session?.version)
    }

    @Test
    fun `the position is carried forward from receipt`() {
        val known = SyncKnowledge().withSnapshot(PlaybackSnapshot(session(positionMs = 10_000)), nowMs = 1_000)
        assertEquals(12_000L, known.positionAt(3_000))
        assertEquals(0L, SyncKnowledge().positionAt(3_000))
    }

    // ---- Yielding ------------------------------------------------------------------------------

    @Test
    fun `yields when another device holds it while this one plays`() {
        assertTrue(shouldYield(session(), ME, playingSessionLocally = true, claimInFlight = false))
    }

    @Test
    fun `does not yield while paused, holding it, or claiming it`() {
        assertFalse(shouldYield(session(), ME, playingSessionLocally = false, claimInFlight = false))
        assertFalse(shouldYield(session(activeDeviceId = ME), ME, playingSessionLocally = true, claimInFlight = false))
        assertFalse(shouldYield(session(), ME, playingSessionLocally = true, claimInFlight = true))
    }

    @Test
    fun `does not yield without a session or a holder`() {
        assertFalse(shouldYield(null, ME, playingSessionLocally = true, claimInFlight = false))
        assertFalse(shouldYield(session(activeDeviceId = null), ME, playingSessionLocally = true, claimInFlight = false))
    }

    // ---- Commands ------------------------------------------------------------------------------

    @Test
    fun `a command lands on its acknowledgement`() {
        assertTrue(commandLanded(PlaybackCommand.PAUSE, "cmd-1", PHONE, true, session(lastCommandId = "cmd-1")))
        assertFalse(commandLanded(PlaybackCommand.PAUSE, "cmd-1", PHONE, true, session(lastCommandId = "cmd-0")))
        assertFalse(commandLanded(PlaybackCommand.PAUSE, "cmd-1", PHONE, true, null))
    }

    @Test
    fun `a transfer lands when the target holds the session`() {
        assertTrue(commandLanded(PlaybackCommand.TRANSFER, "cmd-1", "mac-00001", false, session(activeDeviceId = "mac-00001")))
        assertFalse(commandLanded(PlaybackCommand.TRANSFER, "cmd-1", "mac-00001", false, session()))
        // Only a transfer moves the session; a pause to the holder is not "landed" by it holding.
        assertFalse(commandLanded(PlaybackCommand.PAUSE, "cmd-1", PHONE, true, session()))
    }

    @Test
    fun `a transfer to the device that already held the session lands only on its acknowledgement`() {
        // The phone is the remembered holder, back online with nothing loaded: the session names it
        // before the transfer has done anything there.
        val transfer = SentCommand("c3", PlaybackCommand.TRANSFER, PHONE, targetHeldSession = true)
        fun landed(session: PlaybackSessionDto?) = commandLanded(
            transfer.command, transfer.commandId, transfer.targetDeviceId, transfer.targetHeldSession, session,
        )
        assertFalse(landed(session(live = false, isPlaying = false)))
        assertFalse(landed(session(live = true)))
        // Its claim answers the transfer by id.
        assertTrue(landed(session(live = true, lastCommandId = "c3")))
    }

    // The web's `landedCommands` cases (`session.test.ts`), then the sender's bookkeeping around it
    // (`playback-sync.svelte.ts`'s `pending` and `reachedSeq`, pinned there by the sync tests of the
    // same names).

    private fun next(commandId: String, targetDeviceId: String? = PHONE) =
        SentCommand(commandId, PlaybackCommand.NEXT, targetDeviceId, targetHeldSession = true)

    @Test
    fun `a later command landing settles the ones sent before it to the same device`() {
        // Three taps on Next; the phone acknowledged the burst with the newest id only.
        val pending = listOf(next("c1"), next("c2"), next("c3"))
        assertEquals(listOf("c1", "c2", "c3"), landedCommands(session(lastCommandId = "c3"), pending))
        assertEquals(listOf("c1", "c2"), landedCommands(session(lastCommandId = "c2"), pending))
    }

    @Test
    fun `never settles a command sent after the one that landed, nor one to another device`() {
        val pending = listOf(next("c1", "tv-000001"), next("c2"), next("c3"))
        assertEquals(listOf("c2"), landedCommands(session(lastCommandId = "c2"), pending))
        assertEquals(emptyList<String>(), landedCommands(session(lastCommandId = "c0"), pending))
        assertEquals(emptyList<String>(), landedCommands(null, pending))
    }

    @Test
    fun `a burst acknowledged by its newest command has landed in full`() {
        val sent = SentCommands()
        for (id in listOf("c1", "c2", "c3")) sent.watch(next(id), sent.number())
        assertEquals(listOf("c1", "c2", "c3"), sent.settle(session(lastCommandId = "c3")))
        assertTrue(sent.isEmpty)
    }

    @Test
    fun `an older command answered after a newer one landed has landed too`() {
        // Three quick taps on Next over a slow network: the POSTs are answered c1, c3, c2, and the
        // Mac acknowledged the burst once, with c3.
        val sent = SentCommands()
        val first = sent.number()
        val second = sent.number()
        val third = sent.number()
        sent.watch(next("c1"), first)
        sent.watch(next("c3"), third)
        val acknowledged = session(lastCommandId = "c3")
        assertEquals(listOf("c1", "c3"), sent.settle(acknowledged))
        // No session will ever name c2, and it is not "Couldn't reach" either: it landed before c3.
        sent.watch(next("c2"), second)
        assertEquals(listOf("c2"), sent.settle(acknowledged))
        assertTrue(sent.isEmpty)
        assertFalse(sent.giveUp("c2"))
    }

    @Test
    fun `a command to another device, or sent after the one that landed, keeps waiting until its time is up`() {
        val sent = SentCommands()
        sent.watch(next("c1", "tv-000001"), sent.number())
        sent.watch(next("c2"), sent.number())
        sent.watch(next("c3"), sent.number())
        assertEquals(listOf("c2"), sent.settle(session(lastCommandId = "c2")))
        assertTrue(sent.isWaiting("c1"))
        assertTrue(sent.isWaiting("c3"))
        // Its five seconds are up: it never landed.
        assertTrue(sent.giveUp("c3"))
        assertFalse(sent.isWaiting("c3"))
        assertFalse(sent.isEmpty)
    }

    @Test
    fun `a transfer to the device that already held the session waits for its acknowledgement`() {
        val sent = SentCommands()
        sent.watch(SentCommand("c2", PlaybackCommand.TRANSFER, "tv-000001", targetHeldSession = true), sent.number())
        // Registered after its POST was answered, against the session as it stands: not landed yet,
        // so its five seconds run — and a pick-up that fails there says "Couldn't reach".
        assertEquals(emptyList<String>(), sent.settle(session(activeDeviceId = "tv-000001", live = false)))
        assertTrue(sent.isWaiting("c2"))
        assertEquals(listOf("c2"), sent.settle(session(activeDeviceId = "tv-000001", lastCommandId = "c2")))
        // A transfer to a device that did not hold it still lands by holding it.
        sent.watch(SentCommand("c3", PlaybackCommand.TRANSFER, "mac-00001", targetHeldSession = false), sent.number())
        assertEquals(listOf("c3"), sent.settle(session(activeDeviceId = "mac-00001")))
    }

    @Test
    fun `an optimistic pause, resume or seek shows the outcome at once`() {
        val playing = session()
        val paused = optimisticSession(playing, PlaybackCommand.PAUSE, positionNowMs = 90_000, targetPositionMs = null)
        assertFalse(paused.isPlaying)
        assertEquals(90_000L, paused.positionMs)
        assertTrue(optimisticSession(session(isPlaying = false), PlaybackCommand.RESUME, 90_000, null).isPlaying)
        assertEquals(30_000L, optimisticSession(playing, PlaybackCommand.SEEK, 90_000, 30_000).positionMs)
        // Clamped like any position.
        assertEquals(215_000L, optimisticSession(playing, PlaybackCommand.SEEK, 90_000, 999_000).positionMs)
        // Next cannot be predicted: the song stays, the clock freezes at now.
        val next = optimisticSession(playing, PlaybackCommand.NEXT, 90_000, null)
        assertEquals(123, next.songId)
        assertEquals(90_000L, next.positionMs)
    }

    // ---- Adopting ------------------------------------------------------------------------------

    @Test
    fun `resolves the queue at the session song`() {
        val adoption = resolveAdoption(listOf(120, 123, 131), 1, 123) { "song-$it" }
        assertEquals(Adoption(listOf("song-120", "song-123", "song-131"), 1), adoption)
    }

    @Test
    fun `skips ids this library does not hold, keeping the index on the song`() {
        val adoption = resolveAdoption(listOf(120, 999, 123, 131), 2, 123) { id -> "song-$id".takeIf { id != 999 } }
        assertEquals(Adoption(listOf("song-120", "song-123", "song-131"), 1), adoption)
    }

    @Test
    fun `refuses when the song itself is not here`() {
        assertNull(resolveAdoption(listOf(120, 123, 131), 1, 123) { id -> "song-$id".takeIf { id != 123 } })
        // An index outside the queue (the server never sends one) is refused too.
        assertNull(resolveAdoption(listOf(120, 123), 5, 123) { "song-$it" })
    }

    @Test
    fun `trusts the session's song id over the queue entry at its index`() {
        val adoption = resolveAdoption(listOf(120, 124, 131), 1, 123) { "song-$it" }
        assertEquals(listOf("song-120", "song-123", "song-131"), adoption?.items)
    }

    private fun knowing(session: PlaybackSessionDto?, receivedAtMs: Long) =
        SyncKnowledge().withSnapshot(PlaybackSnapshot(session), receivedAtMs)

    @Test
    fun `an adoption that waited on the library picks up the song the holder has moved on to`() {
        // The tap saw v10, song 123 at 1:00; while a cold start loaded the library, the holder
        // auto-advanced to 131.
        val tapped = session(version = 10, positionMs = 60_000)
        val now = knowing(session(version = 11, songId = 131, queueIndex = 2, positionMs = 5_000), receivedAtMs = 1_000)
        val start = adoptionStartFor(tapped, now, ME, chosenPositionMs = null, nowMs = 4_000)
        assertEquals(131, start?.session?.songId)
        assertEquals(2, start?.session?.queueIndex)
        assertEquals(8_000L, start?.positionMs)
    }

    @Test
    fun `a heartbeat meanwhile starts it where the holder is now, not where the tap saw it`() {
        val tapped = session(version = 10, positionMs = 60_000)
        val now = knowing(session(version = 11, positionMs = 78_000), receivedAtMs = 1_000)
        assertEquals(81_000L, adoptionStartFor(tapped, now, ME, chosenPositionMs = null, nowMs = 4_000)?.positionMs)
    }

    @Test
    fun `with nothing newer, the tap's copy is carried forward to now`() {
        val tapped = session(version = 10, positionMs = 60_000)
        val start = adoptionStartFor(tapped, knowing(tapped, receivedAtMs = 1_000), ME, chosenPositionMs = null, nowMs = 5_000)
        assertEquals(tapped, start?.session)
        assertEquals(64_000L, start?.positionMs)
    }

    @Test
    fun `a scrubbed position holds only for the version it was made on`() {
        val remembered = session(version = 10, positionMs = 60_000, isPlaying = false, live = false)
        val same = adoptionStartFor(remembered, knowing(remembered, 1_000), ME, chosenPositionMs = 30_000, nowMs = 4_000)
        assertEquals(30_000L, same?.positionMs)
        val moved = session(version = 11, songId = 131, queueIndex = 2, positionMs = 5_000, isPlaying = false, live = false)
        val later = adoptionStartFor(remembered, knowing(moved, 1_000), ME, chosenPositionMs = 30_000, nowMs = 4_000)
        assertEquals(5_000L, later?.positionMs)
    }

    @Test
    fun `gives way when the session came here some other way, or is gone`() {
        val tapped = session(version = 10)
        assertNull(adoptionStartFor(tapped, knowing(session(version = 11, activeDeviceId = ME), 1_000), ME, null, 4_000))
        assertNull(adoptionStartFor(tapped, knowing(null, 1_000), ME, null, 4_000))
        // A session this device held before its process died is picked up all the same.
        val mine = session(version = 10, activeDeviceId = ME, isPlaying = false, live = false)
        assertEquals(mine, adoptionStartFor(mine, knowing(mine, 1_000), ME, null, 4_000)?.session)
    }

    // ---- Words ---------------------------------------------------------------------------------

    @Test
    fun `says where the session is`() {
        assertEquals("Playing on Safari on iPhone", deviceLineFor(PlaybackMode.Remote, "Safari on iPhone", isPlaying = true))
        assertEquals("Paused on Safari on iPhone", deviceLineFor(PlaybackMode.Remote, "Safari on iPhone", isPlaying = false))
        assertEquals("Last played on Safari on iPhone", deviceLineFor(PlaybackMode.Remembered, "Safari on iPhone", isPlaying = true))
    }

    @Test
    fun `manages without a name, and says nothing in local mode`() {
        assertEquals("Playing on another device", deviceLineFor(PlaybackMode.Remote, null, isPlaying = true))
        assertEquals("Last played", deviceLineFor(PlaybackMode.Remembered, "  ", isPlaying = false))
        assertNull(deviceLineFor(PlaybackMode.Local, "Safari on iPhone", isPlaying = true))
    }

    // ---- The picker ----------------------------------------------------------------------------

    @Test
    fun `lists the other online devices, the one holding it first and checked`() {
        val devices = listOf(
            device(ME, "Pixel 8", kind = DeviceKind.PHONE),
            device("b-000001", "Chrome on Windows"),
            device("a-000001", "Safari on Mac"),
            device(PHONE, "Safari on iPhone", kind = DeviceKind.PHONE),
            // Offline: the session's old holder is listed by the server, but not in the picker.
            device("z-000001", "Firefox on Linux", online = false),
        )
        val picker = devicePickerFor(devices, session(), ME, PlaybackMode.Remote)
        assertFalse(picker.thisDeviceCurrent)
        assertEquals(listOf("Safari on iPhone", "Chrome on Windows", "Safari on Mac"), picker.others.map { it.name })
        assertEquals(listOf(true, false, false), picker.others.map { it.current })
        assertEquals(listOf("Playing", null, null), picker.others.map { it.status })
        assertEquals(DeviceKind.PHONE, picker.others.first().kind)
    }

    @Test
    fun `marks the holder paused, and nobody while the session is only remembered`() {
        val devices = listOf(device(PHONE, "Safari on iPhone"))
        assertEquals("Paused", devicePickerFor(devices, session(isPlaying = false), ME, PlaybackMode.Remote).others.single().status)
        val remembered = devicePickerFor(devices, session(live = false), ME, PlaybackMode.Remembered)
        assertFalse(remembered.thisDeviceCurrent)
        assertFalse(remembered.others.single().current)
        assertNull(remembered.others.single().status)
    }

    @Test
    fun `checks this device in local mode`() {
        val picker = devicePickerFor(listOf(device("a-000001", "Safari on Mac")), session(activeDeviceId = ME), ME, PlaybackMode.Local)
        assertTrue(picker.thisDeviceCurrent)
        assertFalse(picker.others.single().current)
    }

    // ---- Reports -------------------------------------------------------------------------------

    private fun report(
        positionMs: Long = 10_000,
        isPlaying: Boolean = true,
        claim: Boolean = false,
        inResponseTo: String? = null,
        queue: List<Int>? = null,
        songId: Int = 123,
    ) = PlaybackStateReport(
        deviceId = ME, installId = ME, deviceName = "Pixel 8", deviceKind = DeviceKind.PHONE,
        client = PLAYBACK_CLIENT_ANDROID, claim = claim, inResponseTo = inResponseTo, songId = songId,
        title = "Nightswim", artist = "R.E.M.", album = null, queue = queue, queueIndex = 1,
        positionMs = positionMs, durationMs = 215_000, isPlaying = isPlaying, playbackRate = 1.0,
        radioSeedId = 120, shuffle = false,
    )

    @Test
    fun `a stall's burst of reports says nothing new`() {
        // Sent at 0, playing from 10 s; two seconds later the player is at 12 s — as expected.
        assertTrue(isRedundantReport(report(), lastSentAtMs = 0, next = report(positionMs = 12_000), nowMs = 2_000))
        assertTrue(isRedundantReport(report(), 0, report(positionMs = 13_400), 2_000))
    }

    @Test
    fun `a seek, a pause or a new song is news`() {
        assertFalse(isRedundantReport(report(), 0, report(positionMs = 60_000), 2_000))
        assertFalse(isRedundantReport(report(), 0, report(positionMs = 12_000, isPlaying = false), 2_000))
        assertFalse(isRedundantReport(report(), 0, report(positionMs = 0, songId = 131), 2_000))
        assertFalse(isRedundantReport(null, 0, report(), 2_000))
    }

    @Test
    fun `claims, acknowledgements and queue changes are never redundant`() {
        assertFalse(isRedundantReport(report(), 0, report(positionMs = 12_000, claim = true), 2_000))
        assertFalse(isRedundantReport(report(), 0, report(positionMs = 12_000, inResponseTo = "cmd-1"), 2_000))
        assertFalse(isRedundantReport(report(), 0, report(positionMs = 12_000, queue = listOf(123)), 2_000))
    }

    @Test
    fun `a snapshot naming this device while its queue is loaded is answered at once`() {
        // What makes the holder live again after an API restart or a reconnect.
        assertEquals(SnapshotReport.Heartbeat, snapshotReportFor(session(activeDeviceId = ME), ME, holdsLibraryQueue = true))
        assertEquals(
            SnapshotReport.Heartbeat,
            snapshotReportFor(session(activeDeviceId = ME, isPlaying = false), ME, holdsLibraryQueue = true),
        )
    }

    @Test
    fun `a snapshot still saying this device plays, with nothing of it loaded here, is told it stopped`() {
        // The process died and came back within the server's reconnect grace: the server cannot
        // tell, and would keep the session playing a minute past where the music stopped.
        assertEquals(
            SnapshotReport.Stopped,
            snapshotReportFor(session(activeDeviceId = ME, isPlaying = true), ME, holdsLibraryQueue = false),
        )
        // Already paused (or frozen by the server): nothing to put right.
        assertEquals(
            SnapshotReport.None,
            snapshotReportFor(session(activeDeviceId = ME, isPlaying = false), ME, holdsLibraryQueue = false),
        )
    }

    @Test
    fun `a snapshot of another device's session, or of none, asks nothing of this one`() {
        assertEquals(SnapshotReport.None, snapshotReportFor(session(), ME, holdsLibraryQueue = true))
        assertEquals(SnapshotReport.None, snapshotReportFor(session(), ME, holdsLibraryQueue = false))
        assertEquals(SnapshotReport.None, snapshotReportFor(null, ME, holdsLibraryQueue = false))
    }

    // ---- Identity and the stream ---------------------------------------------------------------

    @Test
    fun `the reconnect schedule backs off from 1 s to 30 s`() {
        assertEquals(listOf(1_000L, 2_000L, 4_000L, 8_000L, 16_000L, 30_000L, 30_000L), (0..6).map(::reconnectDelayMs))
        assertEquals(1_000L, reconnectDelayMs(-1))
    }

    @Test
    fun `a stream that delivered its snapshot comes back within a second`() {
        // The server ends every stream after a few minutes on purpose; that routine end must not
        // wait out whatever the backoff had climbed to on the way to connecting.
        val backoff = ReconnectBackoff()
        assertEquals(listOf(1_000L, 2_000L, 4_000L), List(3) { backoff.nextDelayMs() })
        backoff.connected()
        assertEquals(1_000L, backoff.nextDelayMs())
        assertEquals(2_000L, backoff.nextDelayMs())
    }

    // ---- Commands addressed to this device ------------------------------------------------------

    private val sessionCommands = listOf(
        PlaybackCommand.PAUSE,
        PlaybackCommand.RESUME,
        PlaybackCommand.NEXT,
        PlaybackCommand.PREVIOUS,
        PlaybackCommand.SEEK,
    )

    private fun action(command: String, loaded: Boolean = true, share: Boolean = false, session: Boolean = true) =
        commandActionFor(command, loaded = loaded, shareLoaded = share, hasSession = session)

    @Test
    fun `a loaded library queue takes the command as it is, whatever song the server last heard of`() {
        // A second Next that lands before the first one's report came back must skip again, not
        // reload the server's older copy of the queue and land on the same song twice.
        for (command in sessionCommands) assertEquals(command, CommandAction.Player, action(command))
    }

    @Test
    fun `a player holding nothing loads the session before it acts`() {
        for (command in sessionCommands - PlaybackCommand.PAUSE) {
            assertEquals(command, CommandAction.AdoptFirst, action(command, loaded = false))
        }
        // A pause is answered from what the server knows: there is nothing to stop.
        assertEquals(CommandAction.Player, action(PlaybackCommand.PAUSE, loaded = false))
        assertEquals(CommandAction.Player, action(PlaybackCommand.NEXT, loaded = false, session = false))
    }

    @Test
    fun `a share playing here is not the session, and only a transfer replaces it`() {
        for (command in sessionCommands) assertEquals(command, CommandAction.Ignore, action(command, share = true))
        assertEquals(CommandAction.Adopt, action(PlaybackCommand.TRANSFER, share = true))
        assertEquals(CommandAction.Adopt, action(PlaybackCommand.TRANSFER))
        assertEquals(CommandAction.Adopt, action(PlaybackCommand.TRANSFER, loaded = false, session = false))
    }

    @Test
    fun `names the phone from Settings, else its model`() {
        assertEquals("Alex's Pixel", deviceNameFor(" Alex's Pixel ", "Pixel 8"))
        assertEquals("Pixel 8", deviceNameFor("  ", "Pixel 8"))
        assertEquals("Android", deviceNameFor(null, null))
        assertEquals(PLAYBACK_DEVICE_NAME_CAP, deviceNameFor("x".repeat(100), null).length)
    }

    @Test
    fun `a phone, a tablet, and whatever this app is not built for`() {
        assertEquals(DeviceKind.PHONE, deviceKindFor(411, Configuration.UI_MODE_TYPE_NORMAL))
        assertEquals(DeviceKind.TABLET, deviceKindFor(600, Configuration.UI_MODE_TYPE_NORMAL))
        assertEquals(DeviceKind.UNKNOWN, deviceKindFor(960, Configuration.UI_MODE_TYPE_TELEVISION))
        assertEquals(DeviceKind.UNKNOWN, deviceKindFor(0, Configuration.UI_MODE_TYPE_NORMAL))
    }

    @Test
    fun `device ids follow the wire's pattern`() {
        assertTrue(isValidDeviceId("0f8fad5b-d9cb-469f-a165-70867728950e"))
        assertTrue(isValidDeviceId("abc12345"))
        assertFalse(isValidDeviceId("short"))
        assertFalse(isValidDeviceId("has space here"))
        assertFalse(isValidDeviceId("x".repeat(65)))
    }

    private companion object {
        const val ME = "me-000001"
        const val PHONE = "phone-0001"
    }
}
