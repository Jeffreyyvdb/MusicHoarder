package com.musichoarder.app.data

import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins the playback-sync wire contract (`PlaybackWire.kt`) against literal JSON in the shape the
 * API writes (`MusicHoarder.Api/Playback/PlaybackContracts.cs`, camelCase) — the same examples the
 * web's `wire.test.ts` reads. Three codebases speak this, and Android builds in the field cannot be
 * updated in lockstep, so a renamed field here is a phone that silently stops following the music.
 */
class PlaybackWireTest {

    private val sessionJson = """
        {
          "version": 42,
          "songId": 123,
          "title": "Nightswim", "artist": "R.E.M.", "album": "Automatic for the People",
          "queue": [120, 123, 131],
          "queueIndex": 1,
          "positionMs": 83000,
          "durationMs": 215000,
          "isPlaying": true,
          "playbackRate": 1.0,
          "radioSeedId": 120,
          "shuffle": false,
          "activeDeviceId": "abc12345",
          "activeDeviceName": "Safari on iPhone",
          "live": true,
          "lastCommandId": null,
          "updatedAtUtc": "2026-09-25T10:00:00Z"
        }
    """.trimIndent()

    private val deviceJson = """
        {
          "deviceId": "abc12345",
          "installId": "inst-0001",
          "name": "Safari on iPhone",
          "kind": "phone",
          "client": "web",
          "online": true,
          "isActive": true
        }
    """.trimIndent()

    @Test
    fun `reads the contract's session as it is`() {
        val session = PlaybackJson.decodeFromString<PlaybackSessionDto>(sessionJson)
        assertEquals(42L, session.version)
        assertEquals(123, session.songId)
        assertEquals("Nightswim", session.title)
        assertEquals("R.E.M.", session.artist)
        assertEquals("Automatic for the People", session.album)
        assertEquals(listOf(120, 123, 131), session.queue)
        assertEquals(1, session.queueIndex)
        assertEquals(83_000L, session.positionMs)
        assertEquals(215_000L, session.durationMs)
        assertTrue(session.isPlaying)
        assertEquals(1.0, session.playbackRate, 0.0)
        assertEquals(120, session.radioSeedId)
        assertFalse(session.shuffle)
        assertEquals("abc12345", session.activeDeviceId)
        assertEquals("Safari on iPhone", session.activeDeviceName)
        assertTrue(session.live)
        assertNull(session.lastCommandId)
        assertEquals("2026-09-25T10:00:00Z", session.updatedAtUtc)
    }

    @Test
    fun `reads a device`() {
        val device = PlaybackJson.decodeFromString<PlaybackDevice>(deviceJson)
        assertEquals(PlaybackDevice("abc12345", "inst-0001", "Safari on iPhone", DeviceKind.PHONE, "web", online = true, isActive = true), device)
    }

    @Test
    fun `reads the snapshot and the GET, with or without a session`() {
        val snapshot = PlaybackJson.decodeFromString<PlaybackSnapshot>("""{ "session": $sessionJson, "devices": [$deviceJson] }""")
        assertEquals(42L, snapshot.session?.version)
        assertEquals("abc12345", snapshot.devices.single().deviceId)

        val empty = PlaybackJson.decodeFromString<PlaybackSnapshot>("""{ "session": null, "devices": [] }""")
        assertNull(empty.session)
        assertTrue(empty.devices.isEmpty())
    }

    @Test
    fun `reads the session, devices and command events`() {
        assertEquals(42L, PlaybackJson.decodeFromString<PlaybackSessionEvent>("""{ "session": $sessionJson }""").session?.version)
        assertEquals(1, PlaybackJson.decodeFromString<PlaybackDevicesEvent>("""{ "devices": [$deviceJson] }""").devices.size)

        val command = PlaybackJson.decodeFromString<PlaybackCommandEvent>(
            """
            {
              "commandId": "0b7e7c1c-5a0e-4c1f-9a51-6f4a2f3b9d10",
              "command": "seek",
              "positionMs": 12345,
              "fromDeviceId": "abc12345",
              "fromDeviceName": "Safari on Mac"
            }
            """.trimIndent(),
        )
        assertEquals("0b7e7c1c-5a0e-4c1f-9a51-6f4a2f3b9d10", command.commandId)
        assertEquals(PlaybackCommand.SEEK, command.command)
        assertEquals(12_345L, command.positionMs)
        assertEquals("Safari on Mac", command.fromDeviceName)

        val transfer = PlaybackJson.decodeFromString<PlaybackCommandEvent>(
            """{ "commandId": "c", "command": "transfer", "positionMs": null, "fromDeviceId": "abc12345", "fromDeviceName": null }""",
        )
        assertEquals(PlaybackCommand.TRANSFER, transfer.command)
        assertNull(transfer.positionMs)
    }

    @Test
    fun `reads the state answer and the command replies`() {
        val accepted = PlaybackJson.decodeFromString<PlaybackStateResponse>("""{ "accepted": true, "session": $sessionJson }""")
        assertTrue(accepted.accepted)
        assertEquals(42L, accepted.session?.version)
        assertFalse(PlaybackJson.decodeFromString<PlaybackStateResponse>("""{ "accepted": false, "session": $sessionJson }""").accepted)

        assertEquals("guid-1", PlaybackJson.decodeFromString<PlaybackCommandAccepted>("""{ "commandId": "guid-1" }""").commandId)
        assertEquals("device_offline", PlaybackJson.decodeFromString<PlaybackErrorBody>("""{ "error": "device_offline" }""").error)
        assertEquals("not_active_device", PlaybackJson.decodeFromString<PlaybackErrorBody>("""{ "error": "not_active_device" }""").error)
        assertEquals("no_session", PlaybackJson.decodeFromString<PlaybackErrorBody>("""{ "error": "no_session" }""").error)
    }

    @Test
    fun `ignores what a newer server adds, and survives what an older one leaves out`() {
        val grown = PlaybackJson.decodeFromString<PlaybackSessionDto>(sessionJson.replace("\"live\": true,", "\"live\": true, \"someFutureField\": { \"x\": 1 },"))
        assertEquals(42L, grown.version)

        val sparse = PlaybackJson.decodeFromString<PlaybackSessionDto>("""{ "version": 1, "songId": 5 }""")
        assertEquals(emptyList<Int>(), sparse.queue)
        assertFalse(sparse.live)
        assertFalse(sparse.isPlaying)
        assertEquals(1.0, sparse.playbackRate, 0.0)
        assertNull(sparse.activeDeviceId)

        val bareDevice = PlaybackJson.decodeFromString<PlaybackDevice>("""{ "deviceId": "abc12345" }""")
        assertEquals(DeviceKind.UNKNOWN, bareDevice.kind)
        assertFalse(bareDevice.online)
    }

    @Test
    fun `a report writes every field, the nulls included`() {
        val report = PlaybackStateReport(
            deviceId = "0f8fad5b-d9cb-469f-a165-70867728950e",
            installId = "0f8fad5b-d9cb-469f-a165-70867728950e",
            deviceName = "Pixel 8",
            deviceKind = DeviceKind.PHONE,
            client = PLAYBACK_CLIENT_ANDROID,
            claim = false,
            inResponseTo = null,
            songId = 123,
            title = "Nightswim",
            artist = "R.E.M.",
            album = null,
            queue = null,
            queueIndex = 1,
            positionMs = 83_000,
            durationMs = 215_000,
            isPlaying = true,
            playbackRate = 1.0,
            radioSeedId = null,
            shuffle = false,
        )
        val json = PlaybackJson.parseToJsonElement(PlaybackJson.encodeToString(report)).jsonObject
        assertEquals(
            setOf(
                "deviceId", "installId", "deviceName", "deviceKind", "client", "claim", "inResponseTo",
                "songId", "title", "artist", "album", "queue", "queueIndex", "positionMs", "durationMs",
                "isPlaying", "playbackRate", "radioSeedId", "shuffle",
            ),
            json.keys,
        )
        // `queue: null` is a statement — "unchanged" — so it must be on the wire, not left out.
        assertEquals(JsonNull, json["queue"])
        assertEquals(JsonNull, json["inResponseTo"])
        assertEquals("android", json["client"]?.jsonPrimitive?.content)
        assertEquals("phone", json["deviceKind"]?.jsonPrimitive?.content)
        assertEquals("false", json["claim"]?.jsonPrimitive?.content)

        val claim = PlaybackJson.parseToJsonElement(
            PlaybackJson.encodeToString(report.copy(claim = true, queue = listOf(120, 123, 131), inResponseTo = "cmd-1")),
        ).jsonObject
        assertEquals("true", claim["claim"]?.jsonPrimitive?.content)
        assertEquals(listOf("120", "123", "131"), claim["queue"]?.jsonArray?.map { it.jsonPrimitive.content })
        assertEquals("cmd-1", claim["inResponseTo"]?.jsonPrimitive?.content)
    }

    @Test
    fun `a command body carries the target only when there is one`() {
        val body = PlaybackJson.parseToJsonElement(
            PlaybackJson.encodeToString(PlaybackCommandBody("me-000001", null, PlaybackCommand.PAUSE, null)),
        ) as JsonObject
        assertEquals(setOf("fromDeviceId", "targetDeviceId", "command", "positionMs"), body.keys)
        assertEquals("pause", body["command"]?.jsonPrimitive?.content)
        assertEquals(JsonNull, body["targetDeviceId"])

        val seek = PlaybackJson.parseToJsonElement(
            PlaybackJson.encodeToString(PlaybackCommandBody("me-000001", "abc12345", PlaybackCommand.SEEK, 12_345)),
        ).jsonObject
        assertEquals("abc12345", seek["targetDeviceId"]?.jsonPrimitive?.content)
        assertEquals("12345", seek["positionMs"]?.jsonPrimitive?.content)
    }

    @Test
    fun `the command and event names are the wire's`() {
        assertEquals(
            listOf("pause", "resume", "next", "previous", "seek", "transfer"),
            listOf(
                PlaybackCommand.PAUSE, PlaybackCommand.RESUME, PlaybackCommand.NEXT,
                PlaybackCommand.PREVIOUS, PlaybackCommand.SEEK, PlaybackCommand.TRANSFER,
            ),
        )
        assertEquals(
            listOf("snapshot", "session", "devices", "command", "ping"),
            listOf(PlaybackEvent.SNAPSHOT, PlaybackEvent.SESSION, PlaybackEvent.DEVICES, PlaybackEvent.COMMAND, PlaybackEvent.PING),
        )
    }
}
