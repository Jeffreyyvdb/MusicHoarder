package com.musichoarder.app.data

import android.net.Uri

/**
 * An https share link (`https://host/share/{token}`), delivered by the App Links intent filter or
 * pasted by hand. The token is the whole capability — anonymous, no pairing needed — so the link
 * carries everything: which server to talk to and what it is allowed to see.
 */
data class ShareLink(val origin: String, val token: String) {
    companion object {
        /** Parses a share deep link, or returns null when [raw] is not one. */
        fun parse(raw: String): ShareLink? =
            parseTokenLink(raw, "share")?.let { (origin, token) -> ShareLink(origin, token) }
    }
}

/**
 * An https friend-invite link (`https://host/invite/{token}`). Single-use: opening it only peeks;
 * the token is consumed on the explicit Accept.
 */
data class InviteLink(val origin: String, val token: String) {
    companion object {
        /** Parses an invite deep link, or returns null when [raw] is not one. */
        fun parse(raw: String): InviteLink? =
            parseTokenLink(raw, "invite")?.let { (origin, token) -> InviteLink(origin, token) }
    }
}

/**
 * The shared shape of both links: `http(s)://host[:port]/<head>/<token>` and nothing else. Exactly
 * two path segments, so `/shared/...` (the authenticated friend surface) and deeper paths never
 * match. http is accepted for LAN/emulator instances — same posture as the pairing flow.
 */
private fun parseTokenLink(raw: String, head: String): Pair<String, String>? {
    val uri = runCatching { Uri.parse(raw.trim()) }.getOrNull() ?: return null
    val scheme = uri.scheme?.lowercase() ?: return null
    if (scheme != "https" && scheme != "http") return null
    val host = uri.host?.takeIf { it.isNotBlank() } ?: return null
    val segments = uri.pathSegments
    if (segments.size != 2 || segments[0] != head) return null
    val token = segments[1].trim()
    if (token.isEmpty()) return null
    val origin = buildString {
        append(scheme).append("://").append(host)
        if (uri.port != -1) append(':').append(uri.port)
    }
    return origin to token
}
