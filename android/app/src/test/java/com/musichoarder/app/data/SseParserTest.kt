package com.musichoarder.app.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * Pins the `text/event-stream` framing ([SseParser]) the playback stream is read with — the part of
 * the WHATWG EventSource algorithm a browser does for the web client and this app does by hand. The
 * framing below is what ASP.NET's `TypedResults.ServerSentEvents` writes: `event:` then `data:`,
 * a blank line after each item.
 */
class SseParserTest {

    private fun parse(vararg lines: String): List<SseEvent> {
        val parser = SseParser()
        return lines.mapNotNull(parser::feed)
    }

    @Test
    fun `reads the playback stream's framing`() {
        val events = parse(
            "event: snapshot",
            """data: {"session":null,"devices":[]}""",
            "",
            "event: ping",
            "data: {}",
            "",
            "event: session",
            """data: {"session":{"version":2,"songId":5}}""",
            "",
        )
        assertEquals(
            listOf(
                SseEvent("snapshot", """{"session":null,"devices":[]}"""),
                SseEvent("ping", "{}"),
                SseEvent("session", """{"session":{"version":2,"songId":5}}"""),
            ),
            events,
        )
    }

    @Test
    fun `dispatches only on the blank line`() {
        val parser = SseParser()
        assertNull(parser.feed("event: devices"))
        assertNull(parser.feed("""data: {"devices":[]}"""))
        assertEquals(SseEvent("devices", """{"devices":[]}"""), parser.feed(""))
    }

    @Test
    fun `joins several data lines with a newline`() {
        assertEquals(listOf(SseEvent("command", "{\n\"commandId\":\"c\"\n}")), parse("event: command", "data: {", "data: \"commandId\":\"c\"", "data: }", ""))
    }

    @Test
    fun `drops comments and ignores id and retry`() {
        assertEquals(
            listOf(SseEvent("session", "x")),
            parse(": keep-alive", "id: 7", "retry: 100", "event: session", "data: x", ""),
        )
    }

    @Test
    fun `takes one space after the colon off, and no more`() {
        assertEquals(listOf(SseEvent("message", "x")), parse("data:x", ""))
        assertEquals(listOf(SseEvent("message", " x")), parse("data:  x", ""))
    }

    @Test
    fun `an event without a type is a message, and one without data is nothing`() {
        assertEquals(listOf(SseEvent("message", "hi")), parse("data: hi", ""))
        assertEquals(emptyList<SseEvent>(), parse("event: session", ""))
        // A field with no colon is the whole line as the name, with an empty value.
        assertEquals(listOf(SseEvent("message", "")), parse("data", ""))
    }

    @Test
    fun `a type does not leak into the next event`() {
        assertEquals(
            listOf(SseEvent("session", "a"), SseEvent("message", "b")),
            parse("event: session", "data: a", "", "data: b", ""),
        )
    }
}
