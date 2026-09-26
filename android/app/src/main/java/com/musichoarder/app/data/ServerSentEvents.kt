package com.musichoarder.app.data

/** One dispatched server-sent event: its `event:` type ("message" when none was given) and data. */
data class SseEvent(val event: String, val data: String)

/**
 * The `text/event-stream` framing, line by line — the part of the WHATWG EventSource algorithm a
 * native client needs, which is small enough not to be worth a dependency.
 *
 * Feed it each line without its terminator; a blank line dispatches the event built so far. Comment
 * lines (`:`) are dropped, several `data:` lines join with a newline, one space after the colon is
 * not part of the value, and `id:` / `retry:` are ignored: the playback stream starts every
 * connection with a full snapshot, so there is nothing to resume from and no reason to let the
 * server pick the reconnect schedule.
 */
class SseParser {
    private var eventType: String? = null
    private val data = StringBuilder()
    private var hasData = false

    fun feed(line: String): SseEvent? {
        if (line.isEmpty()) return dispatch()
        if (line.startsWith(':')) return null
        val colon = line.indexOf(':')
        val field = if (colon < 0) line else line.substring(0, colon)
        val raw = if (colon < 0) "" else line.substring(colon + 1)
        val value = if (raw.startsWith(' ')) raw.substring(1) else raw
        when (field) {
            "event" -> eventType = value
            "data" -> {
                if (hasData) data.append('\n')
                data.append(value)
                hasData = true
            }
        }
        return null
    }

    /** An event with no data line is not dispatched at all — the EventSource rule. */
    private fun dispatch(): SseEvent? {
        val event = if (hasData) SseEvent(eventType?.takeIf(String::isNotEmpty) ?: "message", data.toString()) else null
        eventType = null
        data.setLength(0)
        hasData = false
        return event
    }
}
