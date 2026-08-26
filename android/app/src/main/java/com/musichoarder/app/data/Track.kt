package com.musichoarder.app.data

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonPrimitive

/**
 * The slice of `GET /songs` this client cares about. The endpoint returns the whole enrichment
 * record per row; [kotlinx.serialization.json.Json.ignoreUnknownKeys] drops the rest.
 *
 * The set is deliberately narrower than the web's `ApiSong`: the desktop track table's size,
 * format, bitrate, match-confidence and source columns are hidden below 576px there too, so nothing
 * on a phone renders them and they stay out of the model.
 */
@Serializable
data class ApiSong(
    val id: Int,
    val fileName: String = "",
    val title: String? = null,
    val artist: String? = null,
    /** Discrete credited artist names, ';'-joined (e.g. "21 Savage; Travis Scott; Metro Boomin"). */
    val artists: String? = null,
    val albumArtist: String? = null,
    val album: String? = null,
    val year: Int? = null,
    val trackNumber: Int? = null,
    val durationSeconds: Int? = null,
    val durationMs: Int? = null,
    val hasCoverArt: Boolean = false,
    val acquiredAtUtc: String? = null,
    val indexedAtUtc: String? = null,
    val libraryBuiltAtUtc: String? = null,
    val destinationPath: String? = null,
    /** Serialized as a number by `/songs`, but the web's type allows the enum name too. */
    val libraryBuildStatus: JsonPrimitive? = null,
    /** Same number-or-name treatment as [libraryBuildStatus]. */
    val enrichmentStatus: JsonPrimitive? = null,
    /** When you hearted this song here — the local like, independent of Spotify. */
    val likedAtUtc: String? = null,
    /** Spotify's own save date for any collection the track appears in. */
    val spotifyAddedAtUtc: String? = null,
    /** Narrower: only set by a Liked Songs save, so this is the one the Spotify chip can trust. */
    val spotifyLikedAtUtc: String? = null,
    val playCount: Int? = null,
    val lastPlayedAtUtc: String? = null,
    /** "Unknown" | "Released" | "Unreleased" | "LikelyUnreleased", derived server-side. */
    val releaseClassification: String? = null,
    /** How the file got here: "Scanned" | "Downloaded" | "Synced". */
    val originKind: String? = null,
    /** Which collection asked for it: "SpotifyLiked" | "DirectUrl" | … */
    val originSource: String? = null,
    /** "Explicit" when you asked for it, "AlbumFill" when album completion added it. */
    val acquisitionIntent: String? = null,
    val hasSyncedLyrics: Boolean = false,
    val hasPlainLyrics: Boolean = false,
    val lrclibId: String? = null,
    val hasMusicVideo: Boolean = false,
    /**
     * Who shared this track with you. Null means you own it. Pair with [SongsResponse.grantors]
     * for the display name — never build a label from an id alone.
     */
    val sharedByUserId: String? = null,
    /**
     * Server-computed build state, sent for every row. Shared rows always needed it — they carry no
     * [destinationPath], because the grantor's disk layout is not published, so the client has
     * nothing to derive it from — and own rows joined them so the definition lives on the server
     * instead of being re-spelled in each client. See [isBuilt].
     */
    @SerialName("isBuilt") val isBuiltServer: Boolean? = null,
    /**
     * Server-decided "album completion added this", the same fact [acquisitionIntent] spells as an
     * enum name. Absent from a server older than the field, and from shared rows, both of which read
     * as yours. See [Track.isAlbumFill].
     */
    @SerialName("isAlbumFill") val isAlbumFillServer: Boolean? = null,
) {
    /**
     * A song is "built" once it reached the destination library: `LibraryBuildStatus == Done` and a
     * destination path is set. That implies it was enriched and matched first.
     *
     * This is the port of `isBuiltSong` in `frontend/src/lib/album-sections.ts`, and the album and
     * artist grids on both clients filter by it. Without the same filter the phone lists raw scanned
     * rows the web deliberately never shows — including untagged files with no artist or title — so
     * the two clients disagree about what is even in the library.
     */
    val isLiked: Boolean get() = !likedAtUtc.isNullOrBlank()

    val isBuilt: Boolean
        get() {
            isBuiltServer?.let { return it }
            if (destinationPath.isNullOrBlank()) return false
            val status = libraryBuildStatus?.content ?: return false
            return status.toIntOrNull()?.let { it == LIBRARY_BUILD_DONE }
                ?: status.equals("Done", ignoreCase = true)
        }
}

/** `LibraryBuildStatus.Done` — Pending/Copied/Tagged/Done. */
private const val LIBRARY_BUILD_DONE = 3

@Serializable
data class SongsResponse(
    @SerialName("songs") val songs: List<ApiSong> = emptyList(),
    /** One entry per account sharing music with you. Empty when nothing was shared. */
    @SerialName("grantors") val grantors: List<Grantor> = emptyList(),
)

/**
 * An account whose music appears in your library, for the "Shared by …" attribution.
 *
 * [displayName] is null when they never set one; show neutral wording rather than falling back
 * to anything else — the server deliberately does not send their email.
 */
@Serializable
data class Grantor(
    val userId: String,
    val displayName: String? = null,
    val songCount: Int = 0,
)

@Serializable
data class AuthMe(
    val id: String? = null,
    val email: String? = null,
    /**
     * Legacy wire vocabulary ("Owner" | "Demo" | "Friend"). Carried only to label the account
     * switcher — branch on [isAdmin] or [capabilities], never on this.
     */
    val role: String? = null,
    val isAdmin: Boolean = false,
    /** EFFECTIVE capabilities: an admin lists every one, so a check needs no admin special case. */
    val capabilities: List<String> = emptyList(),
    val displayName: String? = null,
)

/** `POST /api/auth/request-link` — `client: "app"` asks for the in-app handoff link flavor. */
@Serializable
data class RequestLinkBody(val email: String, val client: String? = null)

/** Enumeration-safe 200: `magicLinkUrl` only ever comes back from a Development server. */
@Serializable
data class RequestLinkResponse(val ok: Boolean = false, val magicLinkUrl: String? = null)

/** `POST /api/auth/token` — exchanges a magic-link token for the bearer this phone keeps. */
@Serializable
data class TokenExchangeBody(val token: String)

@Serializable
data class AccessTokenResponse(val accessToken: String)

/**
 * `POST /api/auth/webauthn/authenticate/native/begin` — the cookie-free ceremony start.
 *
 * [requestJson] is the WebAuthn request options verbatim, handed to Credential Manager as-is.
 * [state] is the in-flight challenge, data-protected and time-limited by the server: a native
 * client has no cookie jar to round-trip it through, so it carries it and hands it straight back.
 */
@Serializable
data class PasskeyChallengeResponse(val state: String, val requestJson: String)

/**
 * A playable row, with every display field already resolved so the UI never re-decides.
 *
 * Everything the library views sort or filter on is resolved **here**, once per fetch, rather than
 * inside a comparator: the web's sorts call `Date.parse` from within `sort()`, which at four
 * thousand rows is ninety thousand parses per keystroke. After this, every sort key is a primitive
 * and every filter chip is a boolean field read.
 */
data class Track(
    val id: Int,
    val title: String,
    /** The track credit, as tagged. */
    val artist: String,
    val album: String,
    /**
     * The lead artist — `albumArtist ?: artist`. This is the web's `artistOf`, and it is what every
     * library surface displays and groups on, so a compilation lists under its album artist rather
     * than under whoever performs track one.
     */
    val albumArtist: String,
    /** Every credited artist, already split; falls back to [albumArtist] when none were recorded. */
    val artists: List<String>,
    val trackNumber: Int?,
    val year: Int?,
    val durationMs: Long?,
    val durationSeconds: Int,
    val hasCover: Boolean,
    /** `artistLower::albumLower` — the name-level album identity, used to fold split folders back. */
    val nameKey: String,
    val addedAtMs: Long,
    val likedAtMs: Long,
    val spotifyLikedAtMs: Long,
    val spotifyAddedAtMs: Long,
    val lastPlayedAtMs: Long,
    /** Raw stamp, kept so the heart can round-trip the server's value. */
    val likedAtUtc: String?,
    val playCount: Int,
    val isSpotifyLiked: Boolean,
    val isLocalFile: Boolean,
    val isAddedByLink: Boolean,
    val hasVideo: Boolean,
    val hasLyrics: Boolean,
    val isUnreleased: Boolean,
    /** Album completion added this because you already owned another track from the same album. */
    val isAlbumFill: Boolean,
    val needsReview: Boolean,
    /**
     * Absolute stream URL for a track that did not come from the paired library (the anonymous
     * share viewer). When set, the player uses it instead of the paired `/api/mh` route — which
     * also makes playback work with no pairing at all.
     */
    val streamUrl: String? = null,
    /** Absolute artwork URL override, same purpose as [streamUrl]. */
    val artworkUrl: String? = null,
    /**
     * Who shared this track with you; null when this account owns it. Resolve the display name
     * through `LibraryState.grantorOf` — an id on its own is not a label.
     */
    val sharedByUserId: String? = null,
)

const val UNKNOWN_ARTIST = "Unknown artist"
const val UNKNOWN_ALBUM = "Unknown album"

fun ApiSong.toTrack(): Track {
    val trackArtist = artist?.takeIf { it.isNotBlank() } ?: UNKNOWN_ARTIST
    val leadArtist = albumArtist?.takeIf { it.isNotBlank() } ?: trackArtist
    val albumName = album?.takeIf { it.isNotBlank() } ?: UNKNOWN_ALBUM
    val nameKey = "${leadArtist.lowercase()}::${albumName.lowercase()}"

    val acquiredAt = parseIsoUtcMillis(acquiredAtUtc)
    val spotifyLikedAt = parseIsoUtcMillis(spotifyLikedAtUtc)
    val spotifyAddedAt = parseIsoUtcMillis(spotifyAddedAtUtc)
    val likedAt = parseIsoUtcMillis(likedAtUtc)

    return Track(
        id = id,
        // A song that never matched has no title tag; its filename is the only name it has.
        title = title?.takeIf { it.isNotBlank() } ?: fileName.substringBeforeLast('.').ifBlank { "Untitled" },
        artist = trackArtist,
        album = albumName,
        albumArtist = leadArtist,
        artists = discreteArtists(artists, leadArtist),
        trackNumber = trackNumber,
        year = year,
        durationMs = durationMs?.toLong() ?: durationSeconds?.let { it * 1000L },
        durationSeconds = durationSeconds ?: durationMs?.let { it / 1000 } ?: 0,
        hasCover = hasCoverArt,
        nameKey = nameKey,
        addedAtMs = songAddedMillis(
            spotifyAddedAt = spotifyAddedMillis(spotifyLikedAt, spotifyAddedAt),
            acquiredAt = acquiredAt,
            libraryBuiltAt = parseIsoUtcMillis(libraryBuiltAtUtc),
            indexedAt = parseIsoUtcMillis(indexedAtUtc),
        ),
        likedAtMs = songLikedMillis(likedAt, spotifyLikedAt, spotifyAddedAt),
        spotifyLikedAtMs = spotifyLikedAt,
        spotifyAddedAtMs = spotifyAddedMillis(spotifyLikedAt, spotifyAddedAt),
        lastPlayedAtMs = parseIsoUtcMillis(lastPlayedAtUtc),
        likedAtUtc = likedAtUtc?.takeIf { it.isNotBlank() },
        playCount = playCount ?: 0,
        isSpotifyLiked = spotifyLikedAt > 0L,
        isLocalFile = originKind.equals("Scanned", ignoreCase = true),
        isAddedByLink = originSource.equals("DirectUrl", ignoreCase = true),
        hasVideo = hasMusicVideo,
        hasLyrics = hasSyncedLyrics || hasPlainLyrics || !lrclibId.isNullOrBlank(),
        isUnreleased = releaseClassification.equals("Unreleased", ignoreCase = true) ||
            releaseClassification.equals("LikelyUnreleased", ignoreCase = true),
        // The server's answer first; the enum-name comparison is the fallback for an older server.
        isAlbumFill = isAlbumFillServer ?: acquisitionIntent.equals("AlbumFill", ignoreCase = true),
        needsReview = mapEnrichmentState(enrichmentStatus?.content) == EnrichmentState.NeedsReview,
        sharedByUserId = sharedByUserId,
    )
}

/**
 * The `liked` sort key for a heart stamp that may not be the fetched one - an optimistic tap changes
 * `likedAtUtc` without rebuilding the track. See [songLikedMillis] for why the Spotify date can win.
 */
fun Track.likedSortKey(likedAtMillis: Long): Long =
    songLikedMillis(likedAtMillis, spotifyLikedAtMs, spotifyAddedAtMs)
