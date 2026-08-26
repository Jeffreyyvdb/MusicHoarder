import { createPasskey, getPasskeyAssertion } from "$lib/webauthn-client"
import type { PlayerSong } from "$lib/stores/player.svelte"
import type { LyricsProvenance, LyricsSyncStatus } from "$lib/types"

const API_PREFIX = "/api/mh"

export interface ApiStats {
  tracks?: {
    total?: number
    deleted?: number
  }
  storage?: {
    totalBytes?: number
    totalGiB?: number
  }
}

export interface ApiOverviewActivity {
  id: string
  type: "discovered" | "copied" | "enriched" | "review" | "failed"
  track: string
  artist: string
  time: string
}

export interface ApiOverviewScan {
  scanId: string
  totalFiles: number
  processed: number
  newFiles: number
  changedFiles: number
  skippedFiles: number
  failedFiles: number
  isComplete: boolean
  startedAt: string
  completedAt?: string | null
}

export interface ApiOverviewEnrichment {
  runId: string
  totalTracks: number
  processed: number
  enriched: number
  failed: number
  needsReview: number
  isComplete: boolean
  startedAt: string
  completedAt?: string | null
}

export interface ApiOverview {
  sourcePath: string
  destinationPath: string
  scan?: ApiOverviewScan | null
  enrichment?: ApiOverviewEnrichment | null
  job: {
    status: "running" | "completed"
    startedAt: string
    tracksDiscovered: number
    tracksProcessed: number
    tracksFingerprinted: number
    tracksEnriched: number
    tracksBuildEligible: number
    tracksCopied: number
    tracksReview: number
    tracksFailed: number
  }
  recentActivity: ApiOverviewActivity[]
}

export interface ApiSong {
  id: number
  sourcePath: string
  destinationPath?: string | null
  /**
   * Set only on rows shared with you; absent means you own this song. Resolve the display name
   * with `songsStore.grantorOf` — an id on its own is not a label.
   */
  sharedByUserId?: string | null
  /**
   * Server-computed "playable from the built library" — sent for every row. Shared rows have always
   * carried it, because they publish no `destinationPath` for a client to infer it from; own rows
   * gained it so the definition lives in one place rather than one per client. `isBuiltSong` still
   * falls back to deriving it, for a server older than the field.
   */
  isBuilt?: boolean
  fileName: string
  extension?: string | null
  fileSizeBytes: number
  artist?: string | null
  /** Discrete credited artist names, ';'-joined (e.g. "21 Savage; Travis Scott; Metro Boomin"). */
  artists?: string | null
  albumArtist?: string | null
  album?: string | null
  title?: string | null
  year?: number | null
  trackNumber?: number | null
  durationSeconds?: number | null
  fingerprint?: string | null
  isrc?: string | null
  musicBrainzId?: string | null
  musicBrainzReleaseId?: string | null
  spotifyId?: string | null
  acoustIdTrackId?: string | null
  lrclibId?: string | null
  /** Genres, ';'-joined multi-value (e.g. "Hip Hop; Rap"). */
  genre?: string | null
  /** Full release date as an ISO string (YYYY-MM-DD or partial); {@link year} is the coarse form. */
  releaseDate?: string | null
  /** Original (first) release date of the release-group, ISO string. */
  originalReleaseDate?: string | null
  label?: string | null
  catalogNumber?: string | null
  /** Album barcode / UPC. */
  upc?: string | null
  composer?: string | null
  copyright?: string | null
  artistSort?: string | null
  albumArtistSort?: string | null
  enrichmentStatus?: string | number | null
  /** When the scanner first indexed the file (always set). */
  indexedAtUtc?: string | null
  /**
   * When the track entered the collection — stamped once at first index and never rewritten. This is
   * the field "recently added" sorts on: `indexedAtUtc` moves whenever the file changes on disk and
   * `libraryBuiltAtUtc` is cleared by any rebuild, so both drag long-owned tracks back to the top.
   */
  acquiredAtUtc?: string | null
  /** When the track was copied/tagged into the destination library; null until built. */
  libraryBuiltAtUtc?: string | null
  /** Pipeline build state: Pending/Copied/Tagged/Done/Failed (number or string). */
  libraryBuildStatus?: string | number | null
  matchedBy?: string | null
  matchConfidence?: number | null
  matchWarnings?: string[] | null
  /**
   * Whether this is a commercially released recording or unreleased material (leak, snippet, stem,
   * session). Derived server-side by `ReleaseClassifier` — chiefly from the community-tracker
   * category the enrichment match recorded. "Unknown" when nothing in the row says either way.
   */
  releaseClassification?: ReleaseClassification | null
  enrichmentError?: string | null
  originalMetadataCaptured?: boolean | null
  originalArtist?: string | null
  originalAlbumArtist?: string | null
  originalAlbum?: string | null
  originalTitle?: string | null
  originalYear?: number | null
  originalTrackNumber?: number | null
  lyricsStatus?: string | null
  hasSyncedLyrics?: boolean | null
  hasPlainLyrics?: boolean | null
  isInstrumental?: boolean | null
  syncedLyrics?: string | null
  plainLyrics?: string | null
  /** True when an AI transcription exists. The (large) text is fetched on demand via fetchTrackLyrics. */
  hasTranscribedLyrics?: boolean | null
  /** "NotRequested" | "Pending" | "Completed" | "Failed". */
  transcriptionStatus?: string | null
  transcribedAtUtc?: string | null
  transcriptionModel?: string | null
  /** Which lyrics the synced viewer shows when both exist: "Lrclib" | "Transcribed". */
  preferredLyricsSource?: string | null
  /** How much of this song's displayed lyrics came from an AI. */
  lyricsProvenance?: LyricsProvenance | null
  /** What the timing check concluded about the stored LRC. */
  lyricsSyncStatus?: LyricsSyncStatus | null
  lyricsSyncIssue?: string | null
  lyricsSyncOffsetMs?: number | null
  /** Sample rate in Hz (e.g. 44100). Shown in track details when present. */
  sampleRate?: number | null
  /** Bitrate in kbps (e.g. 320, 1411). Shown in track details when present. */
  bitRate?: number | null
  /** Optional explicit album cover URL (e.g. Spotify CDN links on Spotify pages). For
   * library tracks the backend leaves this unset and signals art via {@link hasCoverArt}. */
  albumArt?: string | null
  /** True when the track has resolvable artwork (embedded, or a cover/folder/front.* image in
   * its directory). The image bytes are fetched lazily from {@link getSongCoverUrl}. */
  hasCoverArt?: boolean | null
  /** When the user liked this song; null/undefined = not liked. Also the "recently liked" sort key. */
  likedAtUtc?: string | null
  /** Times playback of this track was started (client-reported). */
  playCount?: number | null
  lastPlayedAtUtc?: string | null
  /** How the file got here: already in the source library, downloaded by MusicHoarder, or instance-synced. */
  originKind?: SongOriginKind | null
  /** Which collection asked for it — the wishlist link behind a download. */
  originSource?: SongOriginSource | null
  /** Names the specific collection ("Liked Songs", a playlist name, a URL host) when there is one. */
  originDetail?: string | null
  /**
   * When the track was saved on Spotify — Spotify's own timestamp, NOT {@link likedAtUtc} (the local
   * heart). Covers any Spotify collection: a playlist you collect contributes the date the track
   * entered *that playlist*, so this answers "Spotify knows about this, and when", not "I saved it".
   * For the latter use {@link spotifyLikedAtUtc}.
   */
  spotifyAddedAtUtc?: string | null
  /**
   * When you saved this track to your Spotify Liked Songs. Narrower than {@link spotifyAddedAtUtc}:
   * a playlist add never sets it, so this is the one the "Spotify Liked" filter can trust.
   */
  spotifyLikedAtUtc?: string | null
  /** True when a music video for this track is on disk and playable. */
  hasMusicVideo?: boolean | null
  /**
   * Whether you asked for this track or album completion added it because you already owned another
   * track from the same album. A stored column, unlike the derived `origin*` fields above.
   *
   * Prefer {@link isAlbumFill}: this one is the enum's name, kept on the wire because shipped
   * Android builds read it.
   */
  acquisitionIntent?: SongAcquisitionIntent | null
  /**
   * Server-decided "album completion added this". The same fact as `acquisitionIntent === 'AlbumFill'`
   * with the string comparison done once, on the side that owns the enum. Absent on rows shared with
   * you (that projection carries no intent) and on a server older than the field, both of which read
   * as "yours" — see {@link isMyMusic}.
   */
  isAlbumFill?: boolean | null
}

/**
 * "Unreleased" = a community tracker says so. "LikelyUnreleased" = every provider ran and none
 * found anything, which is good circumstantial evidence but also catches badly-tagged files and
 * genuinely obscure releases. "Unknown" = no evidence either way.
 */
export type ReleaseClassification =
  | "Unknown"
  | "Released"
  | "Unreleased"
  | "LikelyUnreleased"

export type SongOriginKind = "Scanned" | "Downloaded" | "Synced"
export type SongOriginSource =
  | "None"
  | "SpotifyLiked"
  | "SpotifyPlaylist"
  | "DeezerPlaylist"
  | "DirectUrl"
  | "AlbumCompletion"

export type SongAcquisitionIntent = "Explicit" | "AlbumFill"

/** True when a wishlist link ties this track to Spotify — drives the per-row Source marker. */
export function isSpotifySourced(s: ApiSong): boolean {
  return s.originSource === "SpotifyLiked" || s.originSource === "SpotifyPlaylist"
}

/**
 * True when this track is in your Spotify Liked Songs.
 *
 * Reads {@link ApiSong.spotifyLikedAtUtc} rather than `originSource`, because the two answer
 * different questions: `originSource` names the wishlist collection that *fetched* the file, so it
 * misses every track you already owned when you liked it, and a track in both a playlist and your
 * likes reports the playlist. The date comes from the liked-songs match sweep, which sees all of them.
 */
export function isSpotifyLiked(s: ApiSong): boolean {
  return Boolean(s.spotifyLikedAtUtc)
}

/**
 * True when a scan found this file already sitting in your source library, as opposed to
 * MusicHoarder fetching it. Derived server-side from the file's root, so it follows the file: a
 * quality upgrade that rewrites the path into the download root moves a track out of this set.
 */
export function isLocalFile(s: ApiSong): boolean {
  return s.originKind === "Scanned"
}

/** True when you imported this track yourself from a URL (the Add-from-URL dialog). */
export function isAddedByLink(s: ApiSong): boolean {
  return s.originSource === "DirectUrl"
}

/**
 * True when this track is *yours* — you asked for it, rather than album completion adding it because
 * you already owned another track from the same record. The flat Tracks list and the album-recency
 * sort are built from this; the album views deliberately are not, because a filled album has to look
 * complete there.
 *
 * Two details carry weight:
 *
 *  • A like promotes a filled track. Hearting one is the deliberate act that says "keep this", and
 *    it is the whole payoff of the feature — without it, liking an album-fill track would do nothing
 *    visible. Play counts deliberately do NOT promote: shuffling an album through once would
 *    silently adopt every track on it.
 *  • A row that says nothing about its intent reads as yours. An API too old to send either field,
 *    and the deliberately narrow `SharedSongRowDto` (which carries neither), must degrade to showing
 *    everything rather than to an empty list.
 *
 * The server decides the fill half (`isAlbumFill`); the like half stays here because the store
 * writes `likedAtUtc` optimistically on a heart tap, and a server-computed answer would be stale
 * before the request that refreshed it came back.
 */
export function isMyMusic(s: ApiSong): boolean {
  const fill = s.isAlbumFill ?? s.acquisitionIntent === "AlbumFill"
  return !fill || Boolean(s.likedAtUtc)
}

/** True when a music video for this track is downloaded and playable. */
export function hasMusicVideo(s: ApiSong): boolean {
  return Boolean(s.hasMusicVideo)
}

/**
 * Epoch ms of the Spotify save date, 0 when unknown (sorts such rows last).
 *
 * Prefers the Liked Songs date: for a track that is both liked and in a collected playlist, the
 * moment you saved it is the more meaningful one, and `spotifyAddedAtUtc` may hold the playlist's
 * add date instead.
 */
export function spotifyAddedTime(s: ApiSong): number {
  const iso = s.spotifyLikedAtUtc ?? s.spotifyAddedAtUtc
  const ms = iso ? Date.parse(iso) : NaN
  return Number.isNaN(ms) ? 0 : ms
}

/**
 * Epoch ms of when this song was liked, 0 when it isn't liked at all.
 *
 * `likedAtUtc` records when *this app* learned about the like, not when you made it: the Spotify
 * auto-like sweep stamps every song it matches in one pass with a single `now`, so thousands of
 * rows tie on one instant and "newest liked first" degenerates into the list's tie-break order. An
 * earlier `spotifyAddedAtUtc` is the real like moment, so it wins — same rule as
 * {@link songAddedIso}, and hearts you clicked here (no Spotify save, or a later one) are untouched.
 */
export function songLikedTime(s: ApiSong): number {
  if (!s.likedAtUtc) return 0
  // The Liked Songs date first: a collected playlist's add date is not a like moment, so it must not
  // be allowed to pull a song's "when did I like this" earlier than it really was.
  const iso = oldestIso([s.spotifyLikedAtUtc ?? s.spotifyAddedAtUtc, s.likedAtUtc])
  const ms = iso ? Date.parse(iso) : NaN
  return Number.isNaN(ms) ? 0 : ms
}

/** Short label + long tooltip for the Source column. */
export function songOriginLabel(s: ApiSong): { label: string; title: string } | null {
  const detail = nonEmpty(s.originDetail)
  switch (s.originSource) {
    case "SpotifyLiked":
    case "SpotifyPlaylist":
      return {
        label: "Spotify",
        title: `From your Spotify ${detail ?? "library"}${
          s.originKind === "Downloaded" ? " — downloaded by MusicHoarder" : " — already in your library"
        }`,
      }
    case "DeezerPlaylist":
      return { label: "Deezer", title: `From the Deezer playlist ${detail ?? "(unnamed)"}` }
    case "DirectUrl":
      return { label: "Link", title: `Added from a URL${detail ? ` (${detail})` : ""}` }
    case "AlbumCompletion":
      return {
        label: "Album fill",
        title: `Added to complete ${detail ?? "an album you already owned part of"} — nobody asked for it directly`,
      }
    default:
      break
  }
  switch (s.originKind) {
    case "Downloaded":
      return { label: "Download", title: "Downloaded by MusicHoarder" }
    case "Synced":
      return { label: "Synced", title: "Received from another MusicHoarder instance" }
    case "Scanned":
      return { label: "Library", title: "Found in your source library by a scan" }
    default:
      return null
  }
}

interface SongsResponse {
  count: number
  includeDeleted: boolean
  songs: ApiSong[]
  /** One entry per account whose music appears in `songs`. Empty when nothing was shared with you. */
  grantors?: Grantor[]
}

/** An account whose music appears in your library, for the "Shared by …" attribution. */
export interface Grantor {
  userId: string
  /** Null when they never set a name — the UI picks neutral wording. Never their email. */
  displayName: string | null
  songCount: number
}

/**
 * Who shared what, from the most recent `fetchSongs()`.
 *
 * Module state, mirroring the other client singletons that survive client-side navigation. It is
 * refreshed on every songs fetch and cleared on sign-out, so it cannot outlive the session that
 * produced it.
 *
 * NOT REACTIVE, and it cannot be: this is a plain `.ts` module, so `$state` is unavailable here.
 * UI must read `songsStore.grantors` / `songsStore.grantorOf`, which mirror this into a rune when
 * a fetch resolves. A `$derived` over this variable would compute once before the first fetch
 * lands and then stay clean forever, so the "shared by X" label would never appear.
 */
let grantors: Grantor[] = []

/** Every account currently sharing music with you, in server order. Prefer `songsStore.grantors`. */
export function currentGrantors(): Grantor[] {
  return grantors
}

/** Clear the grantor lookup. Called from sign-out alongside the other client-side resets. */
export function resetGrantors(): void {
  grantors = []
}


/**
 * Error thrown by {@link requestJson} for any non-2xx response. Carries the machine-readable
 * `code` from the API's `{ error, message }` body (e.g. "spotify_editorial_blocked",
 * "invalid_url", "demo_read_only") so callers can branch on the failure kind, while `.message`
 * stays the human-readable text for banners. `code` is null when the body had no `error` field.
 */
export class ApiError extends Error {
  readonly code: string | null
  readonly status: number
  constructor(message: string, code: string | null, status: number) {
    super(message)
    this.name = "ApiError"
    this.code = code
    this.status = status
  }
}

async function requestJson<T>(path: string, init?: RequestInit): Promise<T> {
  const method = (init?.method ?? "GET").toUpperCase()
  const isBodyMethod = method !== "GET" && method !== "HEAD"

  const response = await fetch(`${API_PREFIX}${path}`, {
    ...init,
    headers: isBodyMethod
      ? {
          "content-type": "application/json",
          ...(init?.headers ?? {}),
        }
      : init?.headers,
    cache: "no-store",
  })

  if (!response.ok) {
    let detail = ""
    let code: string | null = null
    try {
      const body = await response.json() as Record<string, unknown>
      if (typeof body.error === "string") code = body.error
      // The demo account is read-only: every mutation is rejected with this code by the API's
      // DemoReadOnlyMiddleware. Surface a human message instead of the raw error code.
      if (body.error === "demo_read_only") {
        throw new ApiError("This action is disabled — the demo account is read-only.", "demo_read_only", response.status)
      }
      // A member tried something their account is not allowed to do at all.
      // "friend_read_only" is the pre-rename code; keep handling it for one release so a browser
      // holding cached JS still shows a sentence instead of a raw error code.
      if (body.error === "member_write_denied" || body.error === "friend_read_only") {
        throw new ApiError(
          "This action is only available to an administrator.",
          body.error,
          response.status
        )
      }
      // A capability the admin has not granted (or has since revoked).
      if (body.error === "capability_required") {
        throw new ApiError(
          "Your account does not have access to that. Ask an administrator to enable it.",
          "capability_required",
          response.status
        )
      }
      if (body.error === "admin_required" || body.error === "owner_required") {
        throw new ApiError("Only an administrator can do that.", body.error, response.status)
      }
      // Anything else reads as raw JSON in error banners — fall back to the code itself as
      // readable text.
      detail =
        (body.message as string) ??
        (typeof body.error === "string" ? body.error.replaceAll("_", " ") : JSON.stringify(body))
    } catch (err) {
      if (err instanceof ApiError) throw err
      // ignore parse errors
    }
    throw new ApiError(detail || `Request failed for ${path}: ${response.status}`, code, response.status)
  }

  return (await response.json()) as T
}

// The running build's version (clean semver, matching the GitHub release / Docker tag). Accepts an
// optional fetch so the root layout load can pass SvelteKit's instance (required during SSR).
export async function getVersion(fetchFn: typeof fetch = fetch): Promise<string> {
  const response = await fetchFn(`${API_PREFIX}/api/version`, { cache: "no-store" })
  if (!response.ok) throw new Error(`Request failed for /api/version: ${response.status}`)
  const body = (await response.json()) as { version: string }
  return body.version
}

// Running build vs latest published release, from the backend's cached GitHub check. `latest` is null
// until the first successful poll (or when the check is disabled / GitHub is unreachable), in which
// case `updateAvailable` is false and no banner shows.
export interface LatestVersionInfo {
  current: string
  latest: string | null
  updateAvailable: boolean
  releaseUrl: string | null
  publishedAt: string | null
}

export async function fetchLatestVersion(): Promise<LatestVersionInfo> {
  const response = await fetch(`${API_PREFIX}/api/version/latest`, { cache: "no-store" })
  if (!response.ok) throw new Error(`Request failed for /api/version/latest: ${response.status}`)
  return (await response.json()) as LatestVersionInfo
}

export type NormalizedEnrichmentStatus =
  | "pending"
  | "processing"
  | "complete"
  | "failed"
  | "needsreview"

export function mapEnrichmentStatus(status?: string | number | null): NormalizedEnrichmentStatus {
  if (typeof status === "number") {
    switch (status) {
      case 1:
        return "complete"
      case 2:
        return "needsreview"
      case 3:
        return "failed"
      default:
        return "pending"
    }
  }

  if (typeof status === "string") {
    const normalized = status.toLowerCase()
    if (normalized === "failed") return "failed"
    if (normalized === "matched" || normalized === "complete") return "complete"
    if (normalized === "needsreview") return "needsreview"
    if (normalized === "running" || normalized === "processing") {
      return "processing"
    }
  }

  return "pending"
}

// ── Album grouping ────────────────────────────────────────────────────────────

const UNKNOWN_ALBUM = "Unknown Album"
const UNKNOWN_ARTIST = "Unknown Artist"

/**
 * One album card exactly as `GET /api/albums` groups it.
 *
 * The grouping rules live on the server now. They used to live here, in full, and in a second full
 * copy in the Android client — which is how the same album added-date rule came to need fixing twice
 * and two separate `each_key_duplicate` crashes came to ship. Anything below this line that looks
 * like a grouping decision is a bug: the client's remaining job is to join the track ids against the
 * songs it already holds.
 */
export interface AlbumSummaryDto {
  /**
   * Stable key, and the `?album=` URL parameter. For built songs this is the destination folder
   * directory — the same unit Navidrome groups on — so one album name split across releases shows as
   * the separate cards the player shows. Unbuilt songs fall back to {@link nameKey}.
   */
  key: string
  /**
   * Every destination folder this card covers — `[key]` for a plain card, and all of the merged
   * folders (representative first) for a merged one. Lets callers resolve an `?album=<folder>`
   * deep-link pointing at a folder that lost the representative election, and tells AlbumPage
   * whether it may pass a single-folder hint to the backend.
   */
  folderKeys: string[]
  /** `artistLower::titleLower` — the name-level identity, and the legacy deep-link shape. */
  nameKey: string
  title: string
  artist: string
  /** The earliest year the tracks agree on — a deluxe reissue's tracks carry the reissue year. */
  year: number | null
  trackCount: number
  durationSeconds: number
  byteSize: number
  genre: string | null
  label: string | null
  catalogNumber: string | null
  upc: string | null
  releaseDate: string | null
  musicBrainzReleaseId: string | null
  /** The first track with artwork; {@link AlbumSummary.coverUrl} is the resolved URL. */
  coverSongId: number | null
  /**
   * Most recent added-date over the tracks that are yours rather than album fill, falling back to
   * all of them for an album that is nothing but fill. What "Recently added" sorts on.
   */
  addedAtUtc: string | null
  playCount: number
  /** The album's tracks, ordered by track number then title. Join against the songs list. */
  trackIds: number[]
}

/** An {@link AlbumSummaryDto} joined against the songs the client holds — see {@link hydrateAlbums}. */
export interface AlbumSummary extends AlbumSummaryDto {
  coverUrl: string | null
  /** The album's songs in {@link AlbumSummaryDto.trackIds} order. */
  songs: ApiSong[]
}

/** Which albums to ask for. Every field narrows the SONGS before they are grouped. */
export interface AlbumQuery {
  /** Only songs that reached the destination library. Default true — what the grid shows. */
  builtOnly?: boolean
  /** Fold cards that are the same album under different destination folders. Default true. */
  merge?: boolean
  artist?: string | null
  /** A year, or `"unknown"` for the no-year bucket. */
  year?: string | null
  unreleased?: boolean
}

export async function fetchAlbums(query: AlbumQuery = {}): Promise<AlbumSummaryDto[]> {
  const params = new URLSearchParams()
  if (query.builtOnly === false) params.set("builtOnly", "false")
  if (query.merge === false) params.set("merge", "false")
  if (query.artist) params.set("artist", query.artist)
  if (query.year) params.set("year", query.year)
  if (query.unreleased) params.set("unreleased", "true")
  const suffix = params.size > 0 ? `?${params}` : ""
  const result = await requestJson<{ albums: AlbumSummaryDto[] }>(`/api/albums${suffix}`)
  return result.albums ?? []
}

/**
 * Join album cards against the songs already in memory.
 *
 * The songs must be the very objects the songs store holds, not copies: the store mutates a row in
 * place when you tap a heart or play a track, and the album views only see that because they hold
 * the same object. Copying here would make a heart tap look like nothing happened.
 */
export function hydrateAlbums(
  albums: AlbumSummaryDto[],
  songsById: Map<number, ApiSong>,
): AlbumSummary[] {
  return albums.map((album) => ({
    ...album,
    coverUrl: album.coverSongId != null ? getSongCoverUrl(album.coverSongId) : null,
    // A song can be missing when the two fetches straddle a library change; dropping it is the
    // honest answer, and the next refresh reconciles.
    songs: album.trackIds
      .map((id) => songsById.get(id))
      .filter((song): song is ApiSong => song !== undefined),
  }))
}

function nonEmpty(value: string | null | undefined): string | null {
  const trimmed = (value ?? "").trim()
  return trimmed.length > 0 ? trimmed : null
}

/**
 * When a song entered the collection, as an ISO string — {@link ApiSong.acquiredAtUtc} when the API
 * has it.
 *
 * Rows predating that column fall back to the OLDEST of the two churn-prone stamps rather than
 * preferring either: a re-index bumps `indexedAtUtc` while a rebuild clears and re-sets
 * `libraryBuiltAtUtc`, so whichever survived un-bumped is the closer guess at the real date.
 *
 * An EARLIER {@link ApiSong.spotifyAddedAtUtc} wins over all of them. `acquiredAtUtc` is the moment
 * the file landed, which for a wishlist download is when the downloader got round to it — so a
 * years-old Spotify save drips in with a today stamp and lands at the top of "recently added" next
 * to things you actually just got. The save date is the moment that song entered *your* collection;
 * the download is just when this app caught up. Only ever pulls the date backwards, so a track you
 * ripped years before saving it on Spotify keeps its acquisition date.
 */
export function songAddedIso(s: ApiSong): string | null {
  return oldestIso([s.spotifyAddedAtUtc, s.acquiredAtUtc ?? fallbackAddedIso(s)])
}

/**
 * Acquisition date for rows predating `acquiredAtUtc` — see {@link songAddedIso}.
 */
function fallbackAddedIso(s: ApiSong): string | null {
  return oldestIso([s.libraryBuiltAtUtc, s.indexedAtUtc])
}

/** The earliest parseable stamp of the candidates, or null when none parse. */
function oldestIso(candidates: (string | null | undefined)[]): string | null {
  let oldest: string | null = null
  let oldestMs = Number.POSITIVE_INFINITY
  for (const candidate of candidates) {
    if (!candidate) continue
    const ms = Date.parse(candidate)
    if (Number.isNaN(ms) || ms >= oldestMs) continue
    oldest = candidate
    oldestMs = ms
  }
  return oldest
}

/** Effective "added to library" time for a song, as epoch ms (0 when unknown). */
export function songAddedTime(s: ApiSong): number {
  const iso = songAddedIso(s)
  return iso ? new Date(iso).getTime() : 0
}

/** Sort albums newest-first by their `addedAtUtc`. Returns a new array. */
export function sortAlbumsByRecency(albums: AlbumSummary[]): AlbumSummary[] {
  return sortAlbums(albums, "recent")
}

function byArtistThenTitle(a: AlbumSummary, b: AlbumSummary): number {
  const artistCmp = a.artist.localeCompare(b.artist)
  return artistCmp !== 0 ? artistCmp : a.title.localeCompare(b.title)
}

/** How the album grid is ordered. */
export type AlbumSortKey = "recent" | "artist" | "title" | "year" | "played"

export const ALBUM_SORT_OPTIONS: { key: AlbumSortKey; label: string }[] = [
  { key: "recent", label: "Recently added" },
  { key: "artist", label: "Artist A–Z" },
  { key: "title", label: "Album title" },
  { key: "year", label: "Year (newest)" },
  { key: "played", label: "Most played" },
]

export function isAlbumSortKey(value: string | null | undefined): value is AlbumSortKey {
  return ALBUM_SORT_OPTIONS.some((o) => o.key === value)
}

/**
 * Order albums for the grid. Every comparator falls back to artist-then-title so albums that tie
 * (no play count, no year, same day added) keep a stable, alphabetical order instead of the
 * arbitrary one the grouping map happened to produce.
 */
export function sortAlbums(albums: AlbumSummary[], key: AlbumSortKey): AlbumSummary[] {
  const added = (a: AlbumSummary) => (a.addedAtUtc ? Date.parse(a.addedAtUtc) : 0)
  const compare: Record<AlbumSortKey, (a: AlbumSummary, b: AlbumSummary) => number> = {
    recent: (a, b) => added(b) - added(a),
    artist: () => 0,
    title: (a, b) => a.title.localeCompare(b.title),
    year: (a, b) => (b.year ?? 0) - (a.year ?? 0),
    played: (a, b) => b.playCount - a.playCount,
  }
  const primary = compare[key]
  return [...albums].sort((a, b) => primary(a, b) || byArtistThenTitle(a, b))
}

// ── Organize-by grouping (Artist / Year) ───────────────────────────────────────

/** Sentinel key/label for songs missing the grouping dimension. */
export const UNKNOWN_GROUP = "Unknown"

/** Aggregated view of all songs sharing a single artist or year. */
export interface GroupSummary {
  /** URL param value: artist name, or year as a string ("Unknown" for the missing bucket). */
  key: string
  /** Display text. */
  label: string
  /** Distinct `(artist::album)` pairs in the group. */
  albumCount: number
  trackCount: number
  /** Sum of fileSizeBytes across the group. */
  byteSize: number
  /** Sum of durationSeconds across the group. */
  durationSeconds: number
  /** Representative cover inputs for <Cover>. */
  coverArtist: string
  coverTitle: string
  coverUrl: string | null
}

/**
 * Stable `(artist, album)` key for a song — `${artistLower}::${titleLower}`, the same shape the
 * server reports as {@link AlbumSummaryDto.nameKey}. Use it to build `/library?album=<key>`
 * deep-links from a single song; the library resolves that shape as well as a folder key.
 */
export function albumKeyForSong(song: ApiSong): string {
  const title = nonEmpty(song.album) ?? UNKNOWN_ALBUM
  const artist = nonEmpty(song.albumArtist) ?? nonEmpty(song.artist) ?? UNKNOWN_ARTIST
  return `${artist.toLowerCase()}::${title.toLowerCase()}`
}

/** Display label used to group a song by artist — matches {@link buildArtistGroups}. */
export function artistLabelForSong(song: ApiSong): string {
  return nonEmpty(song.albumArtist) ?? nonEmpty(song.artist) ?? UNKNOWN_ARTIST
}

/**
 * The artist names a song should be listed under in the Artists view: its discrete credited
 * artists (`artists`, ';'-joined by the enrichment pipeline) when known, else the single
 * `albumArtist ?? artist` label. A multi-artist track ("21 Savage; Travis Scott; Metro Boomin")
 * appears under each individual artist instead of one combined pseudo-artist.
 */
export function discreteArtistsForSong(song: ApiSong): string[] {
  const discrete = (song.artists ?? "")
    .split(";")
    .map((name) => name.trim())
    .filter((name) => name.length > 0)
  return discrete.length > 0 ? discrete : [artistLabelForSong(song)]
}

function albumKeyOf(song: ApiSong): string {
  return albumKeyForSong(song)
}

interface GroupAccumulator extends GroupSummary {
  albumKeys: Set<string>
}

function finalizeGroups(map: Map<string, GroupAccumulator>): GroupSummary[] {
  return Array.from(map.values()).map(({ albumKeys, ...rest }) => ({
    ...rest,
    albumCount: albumKeys.size,
  }))
}

/**
 * Group songs by individual artist ({@link discreteArtistsForSong}), sorted alphabetically.
 * A multi-artist track contributes to each of its credited artists' groups.
 *
 * With `primaryOnly`, only *lead* artists are surfaced: a credited artist is kept only if
 * they are the lead ({@link artistLabelForSong} — `albumArtist ?? artist`) on at least one
 * song. Featured-/guest-only artists who never lead a release are dropped, so the grid
 * shows album artists rather than every performer. A lead artist's card still aggregates
 * the tracks where they only feature.
 */
export function buildArtistGroups(
  songs: ApiSong[],
  opts?: { primaryOnly?: boolean },
): GroupSummary[] {
  const leadKeys = opts?.primaryOnly
    ? new Set(songs.map((s) => artistLabelForSong(s).toLowerCase()))
    : null
  const map = new Map<string, GroupAccumulator>()
  for (const song of songs) {
    for (const label of discreteArtistsForSong(song)) {
      const key = label.toLowerCase()
      if (leadKeys && !leadKeys.has(key)) continue
      let entry = map.get(key)
      if (!entry) {
        entry = {
          key: label,
          label,
          albumCount: 0,
          trackCount: 0,
          byteSize: 0,
          durationSeconds: 0,
          coverArtist: label,
          coverTitle: nonEmpty(song.album) ?? label,
          coverUrl: null,
          albumKeys: new Set(),
        }
        map.set(key, entry)
      }
      entry.trackCount += 1
      entry.byteSize += song.fileSizeBytes ?? 0
      entry.durationSeconds += song.durationSeconds ?? 0
      entry.albumKeys.add(albumKeyOf(song))
      if (!entry.coverUrl) entry.coverUrl = coverUrlForSong(song)
    }
  }
  return finalizeGroups(map).sort((a, b) => a.label.localeCompare(b.label))
}

/** Group songs by `year`, newest first; songs without a year fall into the "Unknown" bucket (sorted last). */
export function buildYearGroups(songs: ApiSong[]): GroupSummary[] {
  const map = new Map<string, GroupAccumulator>()
  for (const song of songs) {
    const hasYear = typeof song.year === "number" && Number.isFinite(song.year)
    const key = hasYear ? String(song.year) : UNKNOWN_GROUP
    let entry = map.get(key)
    if (!entry) {
      entry = {
        key,
        label: key,
        albumCount: 0,
        trackCount: 0,
        byteSize: 0,
        durationSeconds: 0,
        coverArtist: nonEmpty(song.albumArtist) ?? nonEmpty(song.artist) ?? UNKNOWN_ARTIST,
        coverTitle: key,
        coverUrl: null,
        albumKeys: new Set(),
      }
      map.set(key, entry)
    }
    entry.trackCount += 1
    entry.byteSize += song.fileSizeBytes ?? 0
    entry.durationSeconds += song.durationSeconds ?? 0
    entry.albumKeys.add(albumKeyOf(song))
    if (!entry.coverUrl) entry.coverUrl = coverUrlForSong(song)
  }
  return finalizeGroups(map).sort((a, b) => {
    if (a.key === UNKNOWN_GROUP) return 1
    if (b.key === UNKNOWN_GROUP) return -1
    return Number(b.key) - Number(a.key)
  })
}

export async function fetchStats(): Promise<ApiStats> {
  return requestJson<ApiStats>("/stats")
}

export async function fetchOverview(): Promise<ApiOverview> {
  return requestJson<ApiOverview>("/overview")
}

// ── Stats overview (the /stats page) ────────────────────────────────────────
export interface InsightFunnelStage {
  stage: string
  count: number
  pct: number
}
export interface InsightLabelCount {
  status: string
  count: number
}
export interface InsightCoverage {
  count: number
  pct: number
}
export interface LibraryInsights {
  generatedAtUtc: string
  source: { indexed: number; inLibrary: number; inLibraryPct: number; notYetBuilt: number }
  funnel: InsightFunnelStage[]
  covers: { albumCoversAdded: number; builtWithCover: number; builtTracks: number; coveragePct: number }
  lyrics: {
    added: number
    builtWithLyrics: number
    builtTracks: number
    coveragePct: number
    instrumental: number
    notFound: number
    breakdown: InsightLabelCount[]
  }
  wishlist: {
    liked: { total: number; downloaded: number; inLibrary: number; skippedOwned: number }
    all: { total: number; downloaded: number; inLibrary: number }
    sources: number
    funnel: InsightFunnelStage[]
    statusBreakdown: InsightLabelCount[]
  }
  totals: {
    builtTracks: number
    totalHours: number
    totalGiB: number
    distinctArtists: number
    distinctAlbums: number
    duplicates: number
    oldestIndexedUtc: string | null
    newestIndexedUtc: string | null
    byFormat: { format: string; count: number }[]
  }
  top: {
    artists: { name: string; tracks: number }[]
    albums: { album: string; artist: string; tracks: number }[]
  }
  quality: {
    enrichment: InsightLabelCount[]
    confidence: { bucket: string; count: number }[]
    byProvider: { provider: string; total: number; matched: number }[]
    manualApprovals: number
    coverage: {
      fingerprint: InsightCoverage
      musicBrainz: InsightCoverage
      spotify: InsightCoverage
      isrc: InsightCoverage
    }
  }
}

export async function fetchInsights(): Promise<LibraryInsights> {
  return requestJson<LibraryInsights>("/insights")
}

export interface DirectoryMatchNode {
  /** Folder name (the source library root for the top node). */
  name: string
  /** Path relative to the source library root ("" for the root). */
  path: string
  total: number
  matched: number
  needsReview: number
  pending: number
  failed: number
  done: number
  notMatched: number
  matchedPct: number
  /** Files that live directly in this folder (not nested in sub-folders). */
  directFileCount: number
  /** Sum of file sizes rolled up from every song beneath this node. */
  sizeBytes: number
  /** User-tagged "expected low" folder (leaks/unreleased) — pulled out of the work queue. */
  expectedLow: boolean
  children: DirectoryMatchNode[]
}

export async function fetchDirectoryMatchTree(): Promise<DirectoryMatchNode> {
  return requestJson<DirectoryMatchNode>("/library/directory-tree")
}

/** Toggle the current user's "expected low" flag for one source-relative folder path. */
export async function setDirectoryExpectedLow(
  path: string,
  expectedLow: boolean
): Promise<{ path: string; expectedLow: boolean }> {
  return requestJson<{ path: string; expectedLow: boolean }>("/library/directory-preferences", {
    method: "POST",
    body: JSON.stringify({ path, expectedLow })
  })
}

/** Per-file state surfaced in the folder drill-down (mirrors the backend DeriveFileState). */
export type SourceFileState = "written" | "matched" | "review" | "failed" | "queued"

export interface SourceFile {
  id: number
  fileName: string
  extension?: string | null
  fileSizeBytes: number
  enrichmentStatus: string
  libraryBuildStatus: string
  matchConfidence?: number | null
  destinationPath?: string | null
  state: SourceFileState
}

interface DirectoryFilesResponse {
  path: string
  count: number
  files: SourceFile[]
}

/** Lists the songs that live directly inside a single source folder (lazy drill-down). */
export async function fetchFolderFiles(path: string): Promise<SourceFile[]> {
  const result = await requestJson<DirectoryFilesResponse>(
    `/library/directory-tree/files?path=${encodeURIComponent(path)}`
  )
  return result.files ?? []
}

export async function fetchSongs(includeDeleted = false): Promise<ApiSong[]> {
  // One endpoint for every account type: it returns the caller's own rows plus anything shared
  // with them, scoped server-side from their grants. There is no client-side library mode.
  const result = await requestJson<SongsResponse>(`/songs?includeDeleted=${includeDeleted}`)
  grantors = result.grantors ?? []
  return result.songs ?? []
}

// ── Canonical album tracklist (multi-provider, reconciled; full-album view) ─────

/** One reconciled canonical track. `ownedSongId` is null when the user is missing it. */
export interface CanonicalTrack {
  /** Canonical track id — the handle for {@link acquireCanonicalTrack}. */
  id?: number | null
  discNumber: number
  trackNumber: number
  title: string | null
  durationMs: number | null
  musicBrainzRecordingId: string | null
  /** Provider names that corroborate this track (e.g. ["MusicBrainzWeb","Deezer"]). */
  corroboratingProviders: string[]
  corroborationCount: number
  /** True when not every winning-cluster provider backs this track (bonus / disputed). */
  isContested: boolean
  /** The owned ApiSong.id matched to this canonical track, or null if missing. */
  ownedSongId: number | null
}

/** One provider that contributed a tracklist for this album. */
export interface AlbumSource {
  provider: string
  albumId: string | null
  trackCount: number
  /** Whether this source won the reconciliation cluster (vs. a different edition). */
  inWinningCluster: boolean
}

/** Whether an album is matched to a provider album, only in the local library, or not yet checked. */
export type AlbumLinkStatus = "linked" | "localOnly" | "pending"

/** The reconciled canonical tracklist for an album plus how much of it the user owns. */
export interface AlbumTracklist {
  artist: string | null
  album: string | null
  year: number | null
  coverArtUrl: string | null
  /** Consensus track count among the agreeing providers. */
  resolvedTrackCount: number
  /** True when the providers disagree on the album's length. */
  trackCountContested: boolean
  /** Canonical tracks the user owns. */
  ownedCount: number
  /** Canonical track count (= `tracks.length`). */
  totalCount: number
  sources: AlbumSource[]
  tracks: CanonicalTrack[]
}

/** Human-friendly provider labels for the reconciliation source names returned by the API. */
export const PROVIDER_LABELS: Record<string, string> = {
  MusicBrainzWeb: "MusicBrainz",
  SpotifyAPI: "Spotify",
  Deezer: "Deezer",
  AppleMusic: "Apple Music",
}

export function prettyProvider(provider: string): string {
  return PROVIDER_LABELS[provider] ?? provider
}

/**
 * Everything the album detail page renders, in one payload: the album's link status, its reconciled
 * canonical tracklist (when linked), and the latest AI reconciliation grade. Fetched once on
 * navigation so the grade and missing-track info reveal together instead of popping in separately.
 */
export interface AlbumDetailResult {
  status: AlbumLinkStatus
  /** Present only when `status === "linked"`. */
  tracklist: AlbumTracklist | null
  /** Latest reconciliation grade; `{ graded: false }` when unlinked or never graded. */
  grade: AlbumQualityGradeView
}

/**
 * Fetches the combined album detail (link status + tracklist + grade) by artist + album. A
 * non-`linked` status means the owned-only view should be shown.
 */
export async function fetchAlbumDetail(
  artist: string,
  album: string,
  year?: number | null,
  folder?: string | null,
): Promise<AlbumDetailResult> {
  const params = new URLSearchParams({ artist, album })
  // Scope to one destination folder so an album name split across releases shows only this card's
  // release (e.g. a bootleg sharing the name reads as local-only instead of "missing 14 tracks").
  if (year != null) params.set("year", String(year))
  // For built albums the folder is the exact display unit: the backend then matches owned tracks
  // against its direct children only, so unbuilt duplicates can't produce false MISSING rows.
  if (folder) params.set("folder", folder)
  const response = await fetch(`${API_PREFIX}/api/albums/detail?${params.toString()}`, { cache: "no-store" })
  if (response.status === 404) return { status: "pending", tracklist: null, grade: { graded: false } }
  if (!response.ok) throw new Error(`album detail failed: ${response.status}`)
  const body = (await response.json()) as {
    status: AlbumLinkStatus
    tracklist: AlbumTracklist | null
    grade: AlbumQualityGradeView | null
  }
  return { status: body.status, tracklist: body.tracklist ?? null, grade: body.grade ?? { graded: false } }
}

/** Per-album link status for the library grid badges. */
export interface AlbumStatusInfo {
  status: AlbumLinkStatus
  providers: string[]
  /** Latest album-reconciliation AI verdict, when graded (drives the "Wrong" red dot). */
  verdict?: QualityVerdict | null
}

/**
 * Batch link-status for a list of albums. Returns a map keyed by `${artist}::${album}` lowercased
 * (matches {@link AlbumSummary.key}) so callers can look up each card's badge.
 */
export async function fetchAlbumCanonicalStatuses(
  albums: { artist: string; album: string }[]
): Promise<Map<string, AlbumStatusInfo>> {
  const map = new Map<string, AlbumStatusInfo>()
  if (albums.length === 0) return map
  const results = await requestJson<
    { artist: string; album: string; status: AlbumLinkStatus; providers: string[]; verdict?: QualityVerdict | null }[]
  >("/api/albums/canonical-status", { method: "POST", body: JSON.stringify({ albums }) })
  for (const r of results ?? []) {
    map.set(`${r.artist.toLowerCase()}::${r.album.toLowerCase()}`, {
      status: r.status,
      providers: r.providers ?? [],
      verdict: r.verdict ?? null,
    })
  }
  return map
}

// ── Album provenance timeline ────────────────────────────────────────────────────

/** One server-assembled event on the album provenance timeline. */
export interface AlbumTimelineApiEvent {
  key: string
  timeUtc: string
  /** SCAN | METADATA | CANONICAL | AI GRADE | WRITE | CONSOLIDATE | RENAME | YEAR FIX | COVER | APPROVED */
  stage: string
  /** ok | warn | err | info | neutral — matches the track timeline's TimelineTint. */
  tint: string
  /** EnrichmentProvider enum name, or the grading model name for AI GRADE events. */
  provider?: string | null
  /** Confidence / grade score chip (0–100). */
  pct?: number | null
  description: string
  /** Rollup counts: e.g. tracks matched / total tracks in the album. */
  matchedCount?: number | null
  totalCount?: number | null
  /** Rollup span — first/last underlying per-track timestamp. */
  firstAtUtc?: string | null
  lastAtUtc?: string | null
}

export interface AlbumTimelineResponse {
  trackCount: number
  events: AlbumTimelineApiEvent[]
}

/** Chronological provenance timeline for an album (discovery → providers → canonical → writes). */
export async function fetchAlbumTimeline(artist: string, album: string): Promise<AlbumTimelineResponse> {
  const qs = new URLSearchParams({ artist, album }).toString()
  return requestJson<AlbumTimelineResponse>(`/api/albums/timeline?${qs}`)
}

// ── Album reconciliation grading (AI: is the linked album the correct one?) ─────

/** Latest reconciliation grade for one album. */
export interface AlbumQualityGradeView {
  graded: boolean
  canonicalAlbumId?: number
  score?: number
  verdict?: QualityVerdict
  summary?: string | null
  issues?: QualityIssue[]
  ownedTrackCount?: number
  canonicalTrackCount?: number
  gradedAtUtc?: string
  historyCount?: number
}

/** One graded album row for the album-quality workbench. */
export interface AlbumQualityRow {
  canonicalAlbumId: number
  artist?: string | null
  album?: string | null
  year?: number | null
  score: number
  verdict: QualityVerdict
  summary?: string | null
  issues: QualityIssue[]
  ownedTrackCount: number
  canonicalTrackCount: number
  gradedAtUtc: string
  isOutdated?: boolean
}

export interface AlbumQualityOverview {
  gradeableTotal: number
  coverage: number
  library: QualityRollupView
  wrongCount: number
  outdatedCount: number
  worstOffenders: AlbumQualityRow[]
}

export interface AlbumGradeResult {
  outcome: string
  verdict?: QualityVerdict | null
  score?: number | null
  summary?: string | null
  issues?: QualityIssue[]
  gradedAtUtc?: string | null
}

export async function gradeAlbum(artist: string, album: string): Promise<AlbumGradeResult> {
  const qs = new URLSearchParams({ artist, album }).toString()
  return requestJson<AlbumGradeResult>(`/api/albums/quality/grade?${qs}`, { method: "POST" })
}

export async function fetchAlbumQualityOverview(): Promise<AlbumQualityOverview> {
  return requestJson<AlbumQualityOverview>("/api/albums/quality/overview")
}

export async function gradeAllAlbums(): Promise<{ enqueued: number }> {
  return requestJson<{ enqueued: number }>("/api/albums/quality/grade-all", { method: "POST" })
}

export async function gradeOutdatedAlbums(): Promise<{ enqueued: number }> {
  return requestJson<{ enqueued: number }>("/api/albums/quality/grade-outdated", { method: "POST" })
}

export async function fetchAlbumQualityProgress(): Promise<QualityProgress> {
  return requestJson<QualityProgress>("/api/albums/quality/progress")
}

/** Fetches one album's dossier + grade and copies the pretty-printed JSON to the clipboard. */
export async function copyAlbumDossier(artist: string, album: string): Promise<void> {
  const qs = new URLSearchParams({ artist, album }).toString()
  const textPromise = requestJson<unknown>(`/api/albums/quality/export?${qs}`).then((data) =>
    JSON.stringify(data, null, 2)
  )
  if (typeof ClipboardItem !== "undefined" && "write" in navigator.clipboard) {
    const blob = textPromise.then((text) => new Blob([text], { type: "text/plain" }))
    await navigator.clipboard.write([new ClipboardItem({ "text/plain": blob })])
  } else {
    await navigator.clipboard.writeText(await textPromise)
  }
}

export async function startScan(): Promise<{ scanId: string }> {
  return requestJson<{ scanId: string }>("/scan", { method: "POST" })
}

// ── Enrichment controller types ───────────────────────────────────────────────

export interface StepSnapshot {
  status: string
  isPaused: boolean
}

/** Real-time progress snapshot emitted by the SSE stream and the status endpoint. */
export interface ProgressSnapshot {
  status: string
  jobId: string | null
  startedAt: string | null
  completedAt: string | null
  isComplete: boolean
  discovered: number
  scanned: number
  fingerprinted: number
  enriched: number
  needsReview: number
  built: number
  failed: number
  scan: StepSnapshot
  fingerprint: StepSnapshot
  enrich: StepSnapshot
  build: StepSnapshot
  downloaded?: number
  download?: StepSnapshot | null
}

export interface JobStatusResponse {
  progress: ProgressSnapshot
}

export type EnrichmentTriggerResult =
  | { ok: true; jobId: string }
  | { ok: false; status: number; message: string }

// ── Enrichment controller API calls ──────────────────────────────────────────

async function triggerEnrichmentJob(path: string): Promise<EnrichmentTriggerResult> {
  const response = await fetch(`${API_PREFIX}${path}`, {
    method: "POST",
    cache: "no-store",
  })
  const body = await response.json().catch(() => ({})) as Record<string, string>
  if (response.ok) {
    return { ok: true, jobId: body.jobId ?? "" }
  }
  return {
    ok: false,
    status: response.status,
    message: body.message ?? `Request failed: ${response.status}`,
  }
}

export async function triggerEnrichmentScan(): Promise<EnrichmentTriggerResult> {
  return triggerEnrichmentJob("/api/enrichment/scan")
}

export async function triggerFingerprint(): Promise<EnrichmentTriggerResult> {
  return triggerEnrichmentJob("/api/enrichment/fingerprint")
}

export async function triggerEnrich(): Promise<EnrichmentTriggerResult> {
  return triggerEnrichmentJob("/api/enrichment/enrich")
}

export async function triggerBuild(): Promise<EnrichmentTriggerResult> {
  return triggerEnrichmentJob("/api/enrichment/build")
}

export async function cancelJob(): Promise<{ message: string }> {
  return requestJson<{ message: string }>("/api/enrichment/cancel", { method: "POST" })
}

export type RebuildAlbumResult =
  | { ok: true; requeued: number; jobId: string }
  | { ok: false; status: number; message: string }

/**
 * Re-queue an album's already-built tracks so the next build re-copies and re-tags their destination
 * files in place — without re-running enrichment. Used to apply the current tag-writing logic (e.g.
 * album-identity reconciliation) to files that were built before it existed.
 */
export async function rebuildAlbum(artist: string, album: string): Promise<RebuildAlbumResult> {
  const qs = new URLSearchParams({ artist, album }).toString()
  const response = await fetch(`${API_PREFIX}/api/enrichment/rebuild/album?${qs}`, {
    method: "POST",
    cache: "no-store",
  })
  const body = (await response.json().catch(() => ({}))) as Record<string, unknown>
  if (response.ok) {
    return { ok: true, requeued: Number(body.requeued ?? 0), jobId: String(body.jobId ?? "") }
  }
  return {
    ok: false,
    status: response.status,
    message: (body.message as string) ?? `Request failed: ${response.status}`,
  }
}

export type PurgeStatus = "idle" | "running" | "completed" | "failed"
export type PurgeMode = "post-fingerprint" | "all"

export interface PurgeSnapshot {
  status: PurgeStatus
  mode: PurgeMode | null
  jobId: string | null
  startedAt: string | null
  completedAt: string | null
  songsTotal: number
  songsProcessed: number
  filesTotal: number
  filesDeleted: number
  filesFailed: number
  spotifyMatchesCleared: number
  error: string | null
}

export type PurgeStartResult =
  | { ok: true; jobId: string; mode: PurgeMode }
  | { ok: false; status: number; message: string }

async function startPurge(path: string, mode: PurgeMode): Promise<PurgeStartResult> {
  const response = await fetch(`${API_PREFIX}${path}`, {
    method: "POST",
    cache: "no-store",
  })
  const body = (await response.json().catch(() => ({}))) as Record<string, unknown>
  if (response.status === 202) {
    return { ok: true, jobId: String(body.jobId ?? ""), mode }
  }
  return {
    ok: false,
    status: response.status,
    message: (body.message as string) ?? `Request failed: ${response.status}`,
  }
}

export async function purgePostFingerprint(): Promise<PurgeStartResult> {
  return startPurge("/api/enrichment/purge-post-fingerprint", "post-fingerprint")
}

export async function purgeAll(): Promise<PurgeStartResult> {
  return startPurge("/api/enrichment/purge-all", "all")
}

function toPurgeSnapshot(body: Record<string, unknown>): PurgeSnapshot {
  return {
    status: (body.status as PurgeStatus) ?? "idle",
    mode: (body.mode as PurgeMode | null) ?? null,
    jobId: (body.jobId as string | null) ?? null,
    startedAt: (body.startedAt as string | null) ?? null,
    completedAt: (body.completedAt as string | null) ?? null,
    songsTotal: Number(body.songsTotal ?? 0),
    songsProcessed: Number(body.songsProcessed ?? 0),
    filesTotal: Number(body.filesTotal ?? 0),
    filesDeleted: Number(body.filesDeleted ?? 0),
    filesFailed: Number(body.filesFailed ?? 0),
    spotifyMatchesCleared: Number(body.spotifyMatchesCleared ?? 0),
    error: (body.error as string | null) ?? null,
  }
}

export async function fetchPurgeStatus(): Promise<PurgeSnapshot> {
  const body = await requestJson<Record<string, unknown>>("/api/enrichment/purge-status")
  return toPurgeSnapshot(body)
}

export async function pauseStep(step: string): Promise<{ message: string }> {
  return requestJson<{ message: string }>(`/api/enrichment/pause?step=${step}`, { method: "POST" })
}

export async function resumeStep(step: string): Promise<{ message: string }> {
  return requestJson<{ message: string }>(`/api/enrichment/resume?step=${step}`, { method: "POST" })
}

export async function fetchJobStatus(): Promise<JobStatusResponse> {
  return requestJson<JobStatusResponse>("/api/enrichment/status")
}

export interface LibraryAvailability {
  sourceAvailable: boolean
  destinationAvailable: boolean
  sourceDirectory: string
  destinationDirectory: string
  checkedAtUtc: string
}

export async function fetchLibraryAvailability(): Promise<LibraryAvailability> {
  return requestJson<LibraryAvailability>("/api/enrichment/library-availability")
}

/**
 * Opens an SSE connection to `/api/enrichment/progress`.
 * Calls `onSnapshot` for every event, and `onClose` when the server closes
 * the stream (job completed) or a connection error occurs.
 *
 * Returns a cleanup function that closes the EventSource.
 */
export function openProgressStream(
  onSnapshot: (snapshot: ProgressSnapshot) => void,
  onClose?: () => void
): () => void {
  const es = new EventSource(`${API_PREFIX}/api/enrichment/progress`)

  let parseFailures = 0
  es.onmessage = (event) => {
    try {
      onSnapshot(JSON.parse(event.data as string) as ProgressSnapshot)
      parseFailures = 0
    } catch {
      parseFailures += 1
      console.warn(`progress stream: dropped malformed snapshot (${parseFailures})`)
      if (parseFailures >= 3) {
        es.close()
        onClose?.() // callers already treat onClose as "stream ended" and refetch status
      }
    }
  }

  es.onerror = () => {
    es.close()
    onClose?.()
  }

  return () => es.close()
}

export interface ResetEnrichmentResponse {
  id: number
  fileName: string
  enrichmentStatus: number
  libraryBuildStatus: number
  restoredOriginalMetadata: boolean
  message: string
}

export async function resetSongEnrichment(
  songId: number,
  restoreOriginalMetadata = true
): Promise<ResetEnrichmentResponse> {
  return requestJson<ResetEnrichmentResponse>(
    `/songs/${songId}/reset-enrichment?restoreOriginalMetadata=${restoreOriginalMetadata}`,
    { method: "POST" }
  )
}

export interface EnrichSongResponse {
  songId: number
  reset: boolean
  outcome: string
}

/** Enrich a single song now and return the outcome. Pass reset to clear prior attempts first. */
export async function enrichSong(
  songId: number,
  reset = false
): Promise<EnrichSongResponse> {
  return requestJson<EnrichSongResponse>(
    `/api/enrichment/enrich/song/${songId}?reset=${reset}`,
    { method: "POST" }
  )
}

export interface EnrichFolderResponse {
  folder: string
  enqueued: number
  reset: boolean
}

/** Enqueue every song under a source folder (recursively) for enrichment. */
export async function enrichFolder(
  path: string,
  reset = false
): Promise<EnrichFolderResponse> {
  return requestJson<EnrichFolderResponse>(
    `/api/enrichment/enrich/folder?path=${encodeURIComponent(path)}&reset=${reset}`,
    { method: "POST" }
  )
}

export interface TrackLyricsResponse {
  id: number
  lyricsStatus: string
  isInstrumental?: boolean | null
  synced?: string | null
  plain?: string | null
  transcribedSynced?: string | null
  transcribedPlain?: string | null
  transcriptionStatus?: string | null
  transcribedAtUtc?: string | null
  transcriptionModel?: string | null
  preferredLyricsSource?: string | null
  /** How much of the lyrics being displayed came from an AI — drives the badge in every player. */
  lyricsProvenance?: LyricsProvenance | null
  /** True when the stored transcription carries the official words re-timed, not the AI's own guess. */
  transcriptionAlignedToReference?: boolean | null
  /** What the timing check concluded about the stored LRC's timestamps. */
  lyricsSyncStatus?: LyricsSyncStatus | null
  /** Human-readable reason for a Suspect verdict, e.g. "the lyrics run 48s past the end of the track". */
  lyricsSyncIssue?: string | null
  /** The constant shift the AI probe measured and applied, in ms. Non-null means the timing is AI-derived. */
  lyricsSyncOffsetMs?: number | null
  lyricsSyncConfidence?: number | null
  lyricsSyncCheckedAtUtc?: string | null
  romanizedSynced?: string | null
  romanizedPlain?: string | null
  translatedSynced?: string | null
  translatedPlain?: string | null
  detectedLanguage?: string | null
  lyricsTranslationStatus?: string | null
  lyricsTranslatedAtUtc?: string | null
  lyricsTranslationModel?: string | null
  /** True when the stored translation was generated from lyrics that have since changed. */
  lyricsTranslationStale?: boolean | null
}

// Opening the track panel mounts several LyricsPanel variants (card, fullscreen,
// desktop, mobile) that each ask for the same track, so the identical in-flight
// GETs are collapsed into one. Only in-flight requests share a promise — the
// entry is dropped once it settles, so a re-check or a later open still refetches.
const inFlightTrackLyrics = new Map<number, Promise<TrackLyricsResponse>>()

export async function fetchTrackLyrics(trackId: number): Promise<TrackLyricsResponse> {
  const pending = inFlightTrackLyrics.get(trackId)
  if (pending) return pending
  const request = (
    requestJson<TrackLyricsResponse>(`/api/tracks/${trackId}/lyrics`)
  ).finally(() => {
    inFlightTrackLyrics.delete(trackId)
  })
  inFlightTrackLyrics.set(trackId, request)
  return request
}

export interface RecheckLyricsResponse {
  id: number
  /** True when LRCLIB returned something better than what was stored (first lyrics, or an LRC). */
  updated: boolean
  lyricsStatus: string
  hasSyncedLyrics: boolean
  hasPlainLyrics: boolean
  lyricsLastAttemptedAtUtc?: string | null
  lyricsNextRecheckAfterUtc?: string | null
}

/**
 * Ask LRCLIB again for a track whose lyrics are missing or unsynced. LRCLIB is community-contributed
 * and gains lyrics over time, and the background sweep only re-checks on a multi-day backoff — this is
 * the "I can see it on lrclib.net right now" escape hatch. It can only ever add lyrics, never remove
 * them, and re-tags the built file when it finds something.
 */
export async function recheckSongLyrics(songId: number): Promise<RecheckLyricsResponse> {
  return requestJson<RecheckLyricsResponse>(`/songs/${songId}/lyrics/recheck`, { method: "POST" })
}

export interface VerifyLyricsTimingResponse {
  id: number
  lyricsSyncStatus: LyricsSyncStatus
  lyricsSyncIssue?: string | null
  lyricsSyncOffsetMs?: number | null
  lyricsSyncConfidence?: number | null
  lyricsSyncCheckedAtUtc?: string | null
  lyricsProvenance?: LyricsProvenance | null
  /** True when the check rewrote every timestamp — the caller must reload the lyrics text. */
  repaired: boolean
  /** False when the verdict came from the free arithmetic checks alone and cost no API quota. */
  usedAi: boolean
}

/**
 * Check whether a track's stored LRC timestamps actually match its audio, and repair them when the
 * drift turns out to be one constant offset.
 *
 * Cheap by construction: the free arithmetic checks run first, and only if they cannot settle it does
 * the server transcribe a single ~30-second window of the track. It never rewrites the words — a
 * repair moves timestamps only, which is why a repaired track is labelled "AI Enhanced" rather than
 * "AI Generated".
 */
export async function verifyLyricsTiming(songId: number): Promise<VerifyLyricsTimingResponse> {
  return requestJson<VerifyLyricsTimingResponse>(`/songs/${songId}/lyrics/verify-timing`, { method: "POST" })
}

/** Choose which lyrics the synced viewer shows when both an LRCLIB version and an AI transcription exist. */
export async function setPreferredLyricsSource(
  songId: number,
  source: "lrclib" | "transcribed"
): Promise<{ id: number; preferredLyricsSource: string; lyricsTranslationStale?: boolean | null }> {
  return requestJson(`/songs/${songId}/lyrics/preferred?source=${source}`, { method: "POST" })
}

export interface TranscribeLyricsResponse {
  id: number
  synced?: string | null
  plain?: string | null
  transcriptionStatus?: string | null
  transcribedAtUtc?: string | null
  model?: string | null
  hasExistingLyrics?: boolean | null
  /** True when this run re-timed the song's official lyrics rather than inventing its own words. */
  resynced?: boolean | null
  /** The server's (possibly just-updated) display default — "Lrclib" or "Transcribed". */
  preferredLyricsSource?: string | null
  /** True when the re-sync was auto-promoted to the display/file default. */
  promoted?: boolean | null
  /** True when the built destination file was queued for a re-tag with the new lyrics. */
  retagQueued?: boolean | null
  /** True when the fresh transcription made an existing pronunciation/translation stale. */
  lyricsTranslationStale?: boolean | null
}

/**
 * Experimental: transcribe a song's audio via OpenAI Whisper into a synced LRC. The result is stored
 * separately from the LRCLIB lyrics (for side-by-side comparison). When the run re-timed the song's
 * existing official lyrics it is promoted to the display default and the built file is re-tagged;
 * a transcription with no reference lyrics is never promoted automatically.
 */
export async function transcribeSongLyrics(songId: number): Promise<TranscribeLyricsResponse> {
  return requestJson<TranscribeLyricsResponse>(`/songs/${songId}/lyrics/transcribe`, { method: "POST" })
}

export interface TranslateLyricsResponse {
  id: number
  romanizedSynced?: string | null
  romanizedPlain?: string | null
  translatedSynced?: string | null
  translatedPlain?: string | null
  detectedLanguage?: string | null
  lyricsTranslationStatus?: string | null
  lyricsTranslatedAtUtc?: string | null
  model?: string | null
}

/**
 * Generate a pronunciation guide (romanization — Arabizi, pinyin, phonetic respelling…) and an English
 * translation of the song's lyrics via LLM. Display-only: stored apart from the real lyrics and never
 * written into file tags. An already-English song returns detectedLanguage "en" with all-null lyrics.
 */
export async function translateSongLyrics(songId: number): Promise<TranslateLyricsResponse> {
  return requestJson<TranslateLyricsResponse>(`/songs/${songId}/lyrics/translate`, { method: "POST" })
}

export function getSongStreamUrl(songId: number): string {
  return `${API_PREFIX}/songs/${songId}/stream`
}

// ── Music videos (muted clip behind the full-screen player, slaved to the audio clock) ─────

export interface SongVideoInfo {
  status: "Fetching" | "Ready" | "Failed"
  /** videoTime = audioTime + syncOffsetMs / 1000 (positive = the video has an intro). */
  syncOffsetMs: number
  syncSource: "SameSource" | "AutoAligned" | "Manual" | "Unaligned"
  syncConfidence?: number | null
  durationSeconds?: number | null
  youTubeVideoId?: string | null
  fetchedAtUtc: string
  lastError?: string | null
  /** Ready row whose mp4 vanished from disk — the stream would 404; offer a refetch instead. */
  fileMissing?: boolean
}

/**
 * Sync + status info for the song's music video; null when none is attached. Non-404 failures
 * (network blips, 5xx while the API warms up) are retried briefly: the panel's Video tab and the
 * full-screen backdrop both gate on this response, so one dropped fetch at app start would
 * otherwise hide the video until the song changes.
 */
/** Owner reads `/songs/{id}/video`; a Friend session reads the grant-scoped twin. */
function songVideoInfoPath(songId: number): string {
  return `/songs/${songId}/video`
}

export async function getSongVideoInfo(songId: number): Promise<SongVideoInfo | null> {
  for (let attempt = 0; ; attempt++) {
    try {
      return await requestJson<SongVideoInfo>(songVideoInfoPath(songId))
    } catch (err) {
      if (err instanceof ApiError && err.status === 404) return null
      if (attempt >= 2) throw err
      await new Promise((resolve) => setTimeout(resolve, 1000 * (attempt + 1)))
    }
  }
}

/**
 * Like {@link getSongVideoInfo}, but only a definitive answer settles it: a 2xx (info) or a 404
 * (no video attached). Every other failure — a network blip, a proxy 504 during a cold app start,
 * an API restart — is retried with capped backoff until `signal` aborts. The mount-time loads
 * behind the video backdrop and the panel's Video tab use this so one dropped fetch after a page
 * refresh can't masquerade as "no video, fetch it again"; `onRetry` lets them surface an
 * "unavailable, retrying" state meanwhile. Rejects only after an abort.
 */
export async function getSongVideoInfoUntilSettled(
  songId: number,
  opts: { signal: AbortSignal; onRetry?: () => void }
): Promise<SongVideoInfo | null> {
  const RETRY_DELAYS_MS = [1000, 2000, 5000, 10000, 15000]
  for (let attempt = 0; ; attempt++) {
    try {
      return await requestJson<SongVideoInfo>(songVideoInfoPath(songId))
    } catch (err) {
      if (err instanceof ApiError && err.status === 404) return null
      if (opts.signal.aborted) throw err
      opts.onRetry?.()
      const delay = RETRY_DELAYS_MS[Math.min(attempt, RETRY_DELAYS_MS.length - 1)]
      await new Promise((resolve) => setTimeout(resolve, delay))
      if (opts.signal.aborted) throw err
    }
  }
}

export function getSongVideoStreamUrl(songId: number): string {
  return `${API_PREFIX}/songs/${songId}/video/stream`
}

/**
 * Queue a background YouTube fetch of the song's music video (~30–60s; poll
 * {@link getSongVideoInfo} until status leaves "Fetching"). Pass a YouTube URL to pin the exact
 * video; omitted, the backend searches by artist/title.
 */
export async function fetchSongVideo(songId: number, url?: string): Promise<SongVideoInfo> {
  return requestJson<SongVideoInfo>(`/songs/${songId}/video/fetch`, {
    method: "POST",
    body: JSON.stringify({ url: url ?? null }),
  })
}

/**
 * How much a candidate's picture actually moves over its whole runtime, measured from YouTube's
 * storyboard thumbnails before anything is downloaded. `Static` is the album-cover-for-three-minutes
 * case; `Unknown` means the probe could not measure it, never that it is bad.
 */
export type VideoMotion = "Unknown" | "Static" | "LowMotion" | "RealVideo"

export interface SongVideoCandidate {
  videoId: string
  title: string
  channel: string
  durationSeconds: number | null
  score: number
  motion: VideoMotion
  /** Bytes the download would write, or null when yt-dlp reported no size. */
  estimatedBytes: number | null
  /** The upload's own frame is square — a cover image filling the screen. */
  squareSource: boolean
  hasThumbnail: boolean
  /** Already attached to this song. */
  isCurrent: boolean
}

/**
 * Search YouTube for this song's music video and report what each candidate would cost and whether
 * it is a real clip — without downloading any of them. Only the first few carry a measured `motion`;
 * the rest report "Unknown" because probing each one costs a metadata call and a sprite sheet.
 */
export async function getSongVideoCandidates(songId: number): Promise<SongVideoCandidate[]> {
  return requestJson<SongVideoCandidate[]>(`/songs/${songId}/video/candidates`)
}

export interface StoredVideoAuditRow {
  songId: number
  artist: string | null
  title: string | null
  motion: VideoMotion
  /** Mean luma change between sampled frames; null when the file could not be measured. */
  medianFrameDelta: number | null
  fileBytes: number
  durationSeconds: number | null
  youTubeVideoId: string | null
}

export interface StoredVideoAudit {
  measured: number
  staticCount: number
  staticBytes: number
  totalBytes: number
  /** More videos exist beyond the requested limit. */
  more: boolean
  rows: StoredVideoAuditRow[]
}

/**
 * Measure the music videos already on disk and report which are static album covers, with the disk
 * they occupy. Read-only — removing one still goes through {@link deleteSongVideo}.
 */
export async function auditStoredVideos(limit = 100): Promise<StoredVideoAudit> {
  return requestJson<StoredVideoAudit>(`/musicvideos/audit?limit=${Math.round(limit)}`)
}

/**
 * Measure one candidate on demand. {@link getSongVideoCandidates} probes only its top few, so this
 * fills in a verdict for a hit further down the list that the owner is considering.
 */
export async function probeSongVideoCandidate(
  songId: number,
  videoId: string
): Promise<SongVideoCandidate> {
  return requestJson<SongVideoCandidate>(
    `/songs/${songId}/video/probe/${encodeURIComponent(videoId)}`
  )
}

/** Proxy URL for a candidate's YouTube still, so the browser never talks to YouTube directly. */
export function getSongVideoCandidateThumbnailUrl(songId: number, videoId: string): string {
  return `${API_PREFIX}/songs/${songId}/video/thumbnail/${encodeURIComponent(videoId)}`
}

/** Manually nudge the audio↔video sync offset (marks it Manual). */
export async function setSongVideoOffset(songId: number, offsetMs: number): Promise<SongVideoInfo> {
  return requestJson<SongVideoInfo>(`/songs/${songId}/video/offset`, {
    method: "PATCH",
    body: JSON.stringify({ offsetMs }),
  })
}

/** Discard a manual/auto offset and re-run automatic alignment in the background. */
export async function resetSongVideoOffset(songId: number): Promise<SongVideoInfo> {
  return requestJson<SongVideoInfo>(`/songs/${songId}/video/offset`, {
    method: "PATCH",
    body: JSON.stringify({ resetToAuto: true }),
  })
}

/** Remove the song's music video (deletes the downloaded file). */
export async function deleteSongVideo(songId: number): Promise<void> {
  await fetch(`${API_PREFIX}/songs/${songId}/video`, { method: "DELETE", cache: "no-store" })
}

/**
 * Proxy URL for a track's album artwork. The endpoint 404s when the track has no art. Pass `size`
 * (CSS px) to get a small cached WebP thumbnail instead of the full-resolution original — the backend
 * clamps to its nearest size bucket. Omit `size` for the original (downloads / full-screen).
 */
export function getSongCoverUrl(songId: number, size?: number): string {
  const base = `${API_PREFIX}/songs/${songId}/cover`
  return size ? `${base}?size=${Math.round(size)}` : base
}

/** Marks our cover-endpoint URLs so {@link coverThumbUrl} only appends a size to those. */
function isOwnCoverUrl(url: string): boolean {
  return url.startsWith(`${API_PREFIX}/songs/`) && url.includes("/cover")
}

/**
 * Artist portrait endpoint: redirects to a verified Deezer/Spotify portrait (cached server-side by
 * normalized name) or 404s — callers fall back to an album cover / initials tile on error.
 */
export function getArtistImageUrl(name: string): string {
  return `${API_PREFIX}/api/artists/image?name=${encodeURIComponent(name)}`
}

/**
 * Appends a `?size=` thumbnail request to a cover URL **only** when it points at our own cover
 * endpoint; external URLs (e.g. a Spotify CDN image) are returned unchanged.
 */
export function coverThumbUrl(url: string | null | undefined, size: number): string | null {
  if (!url) return null
  if (!isOwnCoverUrl(url)) return url
  const sep = url.includes("?") ? "&" : "?"
  return `${url}${sep}size=${Math.round(size)}`
}

/**
 * Cover URL for a song: an explicit {@link ApiSong.albumArt} (Spotify pages), else the lazy
 * cover endpoint when the backend flagged {@link ApiSong.hasCoverArt}, else null (initials tile).
 */
export function coverUrlForSong(song: ApiSong): string | null {
  if (song.albumArt) return song.albumArt
  return song.hasCoverArt ? getSongCoverUrl(song.id) : null
}

/**
 * Map an {@link ApiSong} to the minimal {@link PlayerSong} the audio store
 * needs, applying the same title/artist fallbacks used across the play call
 * sites. `fallbackArtist` is typically the owning album's artist.
 */
export function toPlayerSong(song: ApiSong, fallbackArtist: string): PlayerSong {
  return {
    id: song.id,
    title: (song.title ?? song.fileName).trim() || song.fileName,
    artist: (song.artist ?? fallbackArtist).trim() || fallbackArtist,
    streamUrl: getSongStreamUrl(song.id),
    coverUrl: coverUrlForSong(song),
    album: song.album ?? null,
  }
}

export function parseSongId(fileItemId: string): number | null {
  if (!fileItemId.startsWith("song:")) return null
  const parsed = Number(fileItemId.slice(5))
  return Number.isFinite(parsed) ? parsed : null
}

// ── Share links (owner-only management; the public consumption lives in share-client.ts) ─────

export interface SongShareView {
  id: number
  token: string
  scope: "Song" | "Album"
  songId: number
  createdAtUtc: string
  title: string
  artist?: string | null
  album?: string | null
}

/**
 * Create a public share link for a song (or its whole album). Idempotent per (song, scope):
 * re-sharing hands back the existing active link instead of minting a new token.
 */
export async function createSongShare(songId: number, scope: "song" | "album"): Promise<SongShareView> {
  return requestJson<SongShareView>("/api/shares", {
    method: "POST",
    body: JSON.stringify({ songId, scope }),
  })
}

export async function listSongShares(): Promise<SongShareView[]> {
  return requestJson<SongShareView[]>("/api/shares")
}

export async function revokeSongShare(id: number): Promise<void> {
  const response = await fetch(`${API_PREFIX}/api/shares/${id}`, { method: "DELETE", cache: "no-store" })
  if (!response.ok) throw new Error(`Could not revoke share (${response.status}).`)
}

/** The public URL a friend opens — same origin, so it works for every deployment. */
export function shareUrl(token: string): string {
  return `${location.origin}/share/${encodeURIComponent(token)}`
}

// ── Friends (owner-only management: invites + grants) ────────────────────────

export interface FriendInviteView {
  id: string
  email: string
  /** Present on creation only — the DB stores a hash, so the URL can't be re-shown later. */
  inviteUrl?: string
  createdAtUtc?: string
  expiresAtUtc: string
  emailSent?: boolean
  emailInLogs?: boolean
}

export interface FriendGrantView {
  id: number
  scope: "Album" | "Artist" | "Library"
  artist?: string | null
  album?: string | null
  createdAtUtc: string
}

/**
 * What an admin may grant a person. Names match the server's `Capability` flags and travel as
 * strings, never a bitmask, so the frontend never does bit math.
 */
export type Capability = "DownloadMusic" | "TrackListening" | "ManageOwnShares" | "Administer"

export interface FriendView {
  id: string
  email: string
  displayName?: string | null
  isDisabled: boolean
  createdAtUtc: string
  lastLoginAtUtc?: string | null
  /** Legacy wire vocabulary ("Owner" | "Demo" | "Friend"). Prefer `isAdmin`. */
  role?: string
  isAdmin?: boolean
  /** EFFECTIVE capabilities — an admin lists every one regardless of what is stored. */
  capabilities?: Capability[]
  grants: FriendGrantView[]
}

/**
 * Create (or rotate) the invite link for an email. Re-inviting the same address replaces the
 * token — the previous link stops working — which is also how "resend" works.
 */
export async function createFriendInvite(email: string, sendEmail?: boolean): Promise<FriendInviteView> {
  return requestJson<FriendInviteView>("/api/people/invites", {
    method: "POST",
    body: JSON.stringify({ email, sendEmail }),
  })
}

export async function listFriendInvites(): Promise<FriendInviteView[]> {
  return requestJson<FriendInviteView[]>("/api/people/invites")
}

export async function revokeFriendInvite(id: string): Promise<void> {
  const response = await fetch(`${API_PREFIX}/api/people/invites/${id}`, { method: "DELETE", cache: "no-store" })
  if (!response.ok) throw new Error(`Could not revoke invite (${response.status}).`)
}

export async function listFriends(): Promise<FriendView[]> {
  return requestJson<FriendView[]>("/api/people")
}

/** Remove a friend: disables their account, kills their sessions, revokes their grants. */
export async function removeFriend(userId: string): Promise<void> {
  const response = await fetch(`${API_PREFIX}/api/people/${userId}`, { method: "DELETE", cache: "no-store" })
  if (!response.ok) throw new Error(`Could not remove friend (${response.status}).`)
}

export async function createFriendGrant(
  userId: string,
  grant: { scope: "album" | "artist" | "library"; artist?: string; album?: string }
): Promise<FriendGrantView> {
  return requestJson<FriendGrantView>(`/api/people/${userId}/grants`, {
    method: "POST",
    body: JSON.stringify(grant),
  })
}

/**
 * Replace a person's capabilities with the given set.
 *
 * The whole desired set is sent, not a delta, so the call is idempotent and two admins toggling
 * different switches cannot interleave into a state neither asked for. Including "Administer"
 * promotes the account; omitting it demotes. The server refuses to change your own capabilities
 * or to demote the last remaining admin.
 */
export async function updatePersonCapabilities(
  userId: string,
  capabilities: Capability[]
): Promise<FriendView> {
  return requestJson<FriendView>(`/api/people/${userId}/capabilities`, {
    method: "PATCH",
    body: JSON.stringify({ capabilities }),
  })
}

export async function revokeFriendGrant(userId: string, grantId: number): Promise<void> {
  const response = await fetch(`${API_PREFIX}/api/people/${userId}/grants/${grantId}`, {
    method: "DELETE",
    cache: "no-store",
  })
  if (!response.ok) throw new Error(`Could not revoke grant (${response.status}).`)
}

// ── Likes + play reporting ───────────────────────────────────────────────────

/** Mark a song as liked. Idempotent — re-liking keeps the original timestamp. */
export async function likeSong(songId: number): Promise<{ id: number; likedAtUtc: string }> {
  const path = `/songs/${songId}/like`
  return requestJson<{ id: number; likedAtUtc: string }>(path, { method: "POST" })
}

export async function unlikeSong(songId: number): Promise<{ id: number; likedAtUtc: null }> {
  const path = `/songs/${songId}/like`
  return requestJson<{ id: number; likedAtUtc: null }>(path, { method: "DELETE" })
}

/**
 * Record a playback start (bumps play count + last-played). Fire-and-forget from the player;
 * demo sessions are write-blocked server-side, so callers should swallow failures.
 */
export async function reportSongPlayed(
  songId: number
): Promise<{ id: number; playCount: number; lastPlayedAtUtc: string }> {
  const path = `/songs/${songId}/played`
  return requestJson<{ id: number; playCount: number; lastPlayedAtUtc: string }>(
    path,
    { method: "POST" }
  )
}

// ── Track review API ──────────────────────────────────────────────────────────

export interface ManualReviewRequest {
  decision: "approve" | "reject"
  rejectReason?: string
  artist?: string
  albumArtist?: string
  album?: string
  title?: string
  year?: number
  trackNumber?: number
}

export interface ManualReviewResponse {
  id: number
  fileName: string
  decision: string
  enrichmentStatus: number
  libraryBuildStatus: number
  artist?: string | null
  album?: string | null
  title?: string | null
  year?: number | null
}

export async function submitManualReview(
  songId: number,
  request: ManualReviewRequest
): Promise<ManualReviewResponse> {
  return requestJson<ManualReviewResponse>(`/songs/${songId}/manual-review`, {
    method: "PATCH",
    body: JSON.stringify(request),
  })
}

export interface BulkApproveResponse {
  minConfidence: number
  approvedCount: number
  approvedIds: number[]
  skippedCount: number
  skippedIds: number[]
}

export async function bulkApprove(
  minConfidence = 0.75
): Promise<BulkApproveResponse> {
  return requestJson<BulkApproveResponse>("/songs/bulk-approve", {
    method: "POST",
    body: JSON.stringify({ minConfidence }),
  })
}

export interface SoftDeleteResponse {
  id: number
  fileName: string
  deletedAtUtc: string
  message: string
}

export async function softDeleteSong(songId: number): Promise<SoftDeleteResponse> {
  return requestJson<SoftDeleteResponse>(`/songs/${songId}`, {
    method: "DELETE",
  })
}

// ── Spotify API ───────────────────────────────────────────────────────────────

export interface SpotifyStatusResponse {
  connected: boolean
  connectedAt?: string | null
  hasCredentials: boolean
  tokenExpired: boolean
}

export interface SpotifyConnectResponse {
  authorizationUrl: string
  state: string
}

export interface SpotifyCredentialsResponse {
  clientId?: string | null
  hasClientSecret: boolean
}

export type SpotifyLibraryMatchStatus = "InLibrary" | "PossibleMatch" | "NotInLibrary"

export interface SpotifyLibraryMatchInfo {
  matchStatus: SpotifyLibraryMatchStatus
  matchedSongId: number | null
  matchConfidence: number | null
  matchedTitle?: string | null
  matchedArtist?: string | null
  matchedEnrichmentStatus?: string | null
}

export interface SpotifyApiTrack {
  spotifyId: string
  title: string
  artist: string
  album: string
  albumArt?: string | null
  durationMs: number
  addedAt: string
  libraryMatch?: SpotifyLibraryMatchInfo | null
  /** True when this Spotify track is already on the owner's wishlist (any status). */
  isInWishlist?: boolean
}

export interface SpotifyLikedSongsApiResponse {
  total: number
  offset: number
  limit: number
  items: SpotifyApiTrack[]
}

export interface SpotifyApiPlaylist {
  spotifyId: string
  name: string
  description?: string | null
  imageUrl?: string | null
  trackCount: number
  ownerName?: string | null
}

export interface SpotifyPlaylistsApiResponse {
  items: SpotifyApiPlaylist[]
}

export interface SpotifyPlaylistTracksApiResponse {
  total: number
  offset: number
  limit: number
  items: SpotifyApiTrack[]
}

export async function fetchSpotifyStatus(): Promise<SpotifyStatusResponse> {
  return requestJson<SpotifyStatusResponse>("/api/spotify/status")
}

export async function fetchSpotifyConnectUrl(): Promise<SpotifyConnectResponse> {
  return requestJson<SpotifyConnectResponse>("/api/spotify/connect")
}

export async function disconnectSpotify(): Promise<{ message: string }> {
  return requestJson<{ message: string }>("/api/spotify/disconnect", { method: "DELETE" })
}

export async function fetchSpotifyCredentials(): Promise<SpotifyCredentialsResponse> {
  return requestJson<SpotifyCredentialsResponse>("/api/spotify/credentials")
}

export async function saveSpotifyCredentials(clientId: string, clientSecret: string): Promise<{ message: string }> {
  return requestJson<{ message: string }>("/api/spotify/credentials", {
    method: "PUT",
    body: JSON.stringify({ clientId, clientSecret }),
  })
}

export async function fetchSpotifyLikedSongs(offset = 0, limit = 50): Promise<SpotifyLikedSongsApiResponse> {
  return requestJson<SpotifyLikedSongsApiResponse>(`/api/spotify/liked-songs?offset=${offset}&limit=${limit}`)
}

export async function fetchSpotifyPlaylists(): Promise<SpotifyPlaylistsApiResponse> {
  return requestJson<SpotifyPlaylistsApiResponse>("/api/spotify/playlists")
}

export async function fetchSpotifyPlaylistTracks(playlistId: string, offset = 0, limit = 50): Promise<SpotifyPlaylistTracksApiResponse> {
  return requestJson<SpotifyPlaylistTracksApiResponse>(
    `/api/spotify/playlists/${playlistId}/tracks?offset=${offset}&limit=${limit}`
  )
}

// ── Wishlist API ──────────────────────────────────────────────────────────────

export type WishlistItemStatus =
  | "Pending"
  | "SkippedOwned"
  | "Downloading"
  | "Downloaded"
  | "Failed"
  | "NotFound"

export type WishlistSourceType = "LikedSongs" | "Playlist" | "DeezerPlaylist"

export interface WishlistItem {
  id: number
  /** Null for Deezer-sourced items — use {@link deezerTrackId} instead. */
  spotifyTrackId: string | null
  /** Set for tracks sourced from a Deezer playlist; null for Spotify-sourced items. */
  deezerTrackId?: string | null
  title: string
  artist: string
  album?: string | null
  isrc?: string | null
  durationMs: number
  albumArt?: string | null
  spotifyAddedAtUtc?: string | null
  status: WishlistItemStatus
  downloadProvider?: string | null
  downloadedFilePath?: string | null
  downloadedSongId?: number | null
  attemptCount: number
  lastError?: string | null
  libraryEnrichmentStatus?: string | null
  libraryBuildStatus?: string | null
  createdAtUtc: string
  updatedAtUtc: string
  /** Who queued it: you, or album completion filling in a record you already owned part of. */
  origin?: WishlistItemOrigin | null
}

/** Which slice of the wishlist to page through. Defaults to what you asked for. */
export type WishlistSourceFilter = "user" | "albumfill" | "all"

export type WishlistItemOrigin = "UserRequested" | "AlbumCompletion"

export interface WishlistItemsResponse {
  total: number
  offset: number
  limit: number
  items: WishlistItem[]
}

export interface WishlistSource {
  id: number
  sourceType: WishlistSourceType
  spotifyPlaylistId?: string | null
  /** Set for Deezer-playlist sources; null for Spotify sources. */
  deezerPlaylistId?: string | null
  /** Which upstream this source syncs from. */
  provider: "spotify" | "deezer"
  name: string
  imageUrl?: string | null
  autoSync: boolean
  lastSyncedAtUtc?: string | null
  createdAtUtc: string
  itemCount: number
}

export interface AddWishlistSourceResult {
  sourceId: number
  /** The snapshot runs in the background; tracks appear on the wishlist progressively. */
  queued: boolean
}

/**
 * One page of the wishlist. `source` defaults to `user` server-side: album-completion items are a
 * background drip that can outnumber real wishlist rows by orders of magnitude, and since this is
 * paged they would otherwise fill every page before a track you asked for appeared.
 */
export async function fetchWishlist(
  status?: WishlistItemStatus,
  offset = 0,
  limit = 50,
  source?: WishlistSourceFilter,
): Promise<WishlistItemsResponse> {
  const params = new URLSearchParams({ offset: String(offset), limit: String(limit) })
  if (status) params.set("status", status)
  if (source) params.set("source", source)
  return requestJson<WishlistItemsResponse>(`/api/wishlist?${params.toString()}`)
}

export interface AlbumCompletionRunResult {
  albumsExamined: number
  tracksQueued: number
  albumsFilled: number
  albumsSkipped: number
  albumsAlreadyComplete: number
  /** Set when the pass stopped before examining anything; null when it genuinely ran. */
  idleReason: string | null
}

/**
 * Run one album-completion pass now. The background loop only ticks hourly, so this is what makes
 * flipping the toggle do something you can see — and the result is what lets us explain a zero,
 * which is a perfectly normal outcome.
 */
export async function runAlbumCompletion(): Promise<AlbumCompletionRunResult> {
  return requestJson<AlbumCompletionRunResult>("/api/wishlist/complete-albums", { method: "POST" })
}

/** Plain-English summary of a completion pass, for the Wishlist banner. */
export function albumCompletionSummary(r: AlbumCompletionRunResult): string {
  switch (r.idleReason) {
    case "downloads-disabled":
      return "Album completion needs wishlist downloads enabled first."
    case "disabled":
      return "Album completion is off."
    case "at-pending-ceiling":
      return "Already enough queued — completion pauses until the downloader catches up."
    case "library-empty":
      return "No music in your library yet — nothing to complete."
    case "no-canonical-albums":
      return "No album has a reconciled tracklist yet. That's built in the background as tracks finish enriching; try again in a few minutes."
    case "no-candidates":
      return "Nothing new to check — every eligible album has been looked at recently."
    default:
      break
  }

  if (r.tracksQueued > 0) {
    const albums = `${r.albumsFilled} album${r.albumsFilled === 1 ? "" : "s"}`
    return `Queued ${r.tracksQueued} missing track${r.tracksQueued === 1 ? "" : "s"} across ${albums}.`
  }

  // A zero here is a real answer, not a failure — say which kind.
  const parts: string[] = []
  if (r.albumsAlreadyComplete > 0) parts.push(`${r.albumsAlreadyComplete} already complete`)
  if (r.albumsSkipped > 0) parts.push(`${r.albumsSkipped} skipped (compilations or singles)`)
  const detail = parts.length > 0 ? ` — ${parts.join(", ")}` : ""
  return `Checked ${r.albumsExamined} album${r.albumsExamined === 1 ? "" : "s"}, nothing to queue${detail}.`
}

/** Queue one canonical album track you're missing (the album page's "Get this track"). */
export async function acquireCanonicalTrack(
  canonicalTrackId: number,
): Promise<{ wishlistItemId: number; alreadyQueued?: boolean; requeued?: boolean; status?: string }> {
  return requestJson(`/api/albums/tracks/${canonicalTrackId}/acquire`, { method: "POST" })
}

export async function fetchWishlistSources(): Promise<{ sources: WishlistSource[] }> {
  return requestJson<{ sources: WishlistSource[] }>("/api/wishlist/sources")
}

export async function addWishlistSource(
  type: WishlistSourceType,
  options: { playlistId?: string; deezerPlaylistId?: string; autoSync?: boolean } = {}
): Promise<AddWishlistSourceResult> {
  // Send both `type`/`sourceType` and `playlistId`/`deezerPlaylistId` so the request satisfies the
  // established Spotify shape and the new Deezer contract regardless of which field names the API
  // binds — a Deezer subscribe carries `deezerPlaylistId`, a Spotify one carries `playlistId`.
  return requestJson<AddWishlistSourceResult>("/api/wishlist/sources", {
    method: "POST",
    body: JSON.stringify({
      type,
      sourceType: type,
      playlistId: options.playlistId,
      deezerPlaylistId: options.deezerPlaylistId,
      autoSync: options.autoSync ?? false,
    }),
  })
}

export async function setWishlistSourceAutoSync(id: number, autoSync: boolean): Promise<{ id: number; autoSync: boolean }> {
  return requestJson<{ id: number; autoSync: boolean }>(`/api/wishlist/sources/${id}`, {
    method: "PATCH",
    body: JSON.stringify({ autoSync }),
  })
}

export async function removeWishlistSource(id: number): Promise<{ message: string }> {
  return requestJson<{ message: string }>(`/api/wishlist/sources/${id}`, { method: "DELETE" })
}

export async function retryWishlistItem(id: number): Promise<{ id: number; status: WishlistItemStatus }> {
  return requestJson<{ id: number; status: WishlistItemStatus }>(`/api/wishlist/items/${id}/retry`, { method: "POST" })
}

export async function removeWishlistItem(id: number): Promise<{ message: string }> {
  return requestJson<{ message: string }>(`/api/wishlist/items/${id}`, { method: "DELETE" })
}

export async function retryFailedWishlistItems(): Promise<{ reset: number }> {
  return requestJson<{ reset: number }>("/api/wishlist/items/retry-failed", { method: "POST" })
}

export async function triggerWishlistDownload(): Promise<{ jobId: string }> {
  return requestJson<{ jobId: string }>("/api/wishlist/download", { method: "POST" })
}

// ── Add from URL (single-track import) ─────────────────────────────────────────
// Paste a Spotify track or YouTube video URL → resolve to editable metadata → queue a download.
// The download flows through the normal pipeline and (on a Push instance) auto-syncs to the public
// receiver once built.

export type ImportSource = "spotify" | "youtube"

export interface ImportResolveResult {
  source: ImportSource
  title: string
  artist: string
  album: string | null
  durationMs: number
  coverUrl: string | null
  spotifyTrackId: string | null
  isrc: string | null
  sourceUrl: string | null
}

export interface ImportTrackPayload {
  source: ImportSource
  title: string
  artist: string
  album?: string | null
  durationMs?: number | null
  coverUrl?: string | null
  spotifyTrackId?: string | null
  isrc?: string | null
  sourceUrl?: string | null
  /** "Also download the music video" — omit/null to follow the server default. */
  downloadMusicVideo?: boolean | null
}

export interface ImportTrackResult {
  wishlistItemId: number
  jobStarted: boolean
  jobId: string | null
}

/** Resolve a pasted Spotify/YouTube track URL into editable, download-ready metadata. */
export async function resolveImportUrl(url: string): Promise<ImportResolveResult> {
  return requestJson<ImportResolveResult>("/api/import/resolve", {
    method: "POST",
    body: JSON.stringify({ url }),
  })
}

/** Queue a resolved (possibly edited) track for download. */
export async function importTrack(payload: ImportTrackPayload): Promise<ImportTrackResult> {
  return requestJson<ImportTrackResult>("/api/import", {
    method: "POST",
    body: JSON.stringify(payload),
  })
}

// ── Discover (editorial / chart playlists, sourced from Deezer) ────────────────
// Spotify's API blocks editorial playlists for personal apps, so the browse/chart/search
// catalog comes from Deezer. Subscribing routes through the existing wishlist source pipeline
// (see addWishlistSource with type "DeezerPlaylist").

export interface DiscoverGenre {
  id: number
  name: string
  pictureUrl: string | null
}

export interface DiscoverPlaylistSummary {
  id: string
  title: string
  description: string | null
  coverUrl: string | null
  trackCount: number
  creatorName: string | null
  /** True when the user already subscribes to this playlist (a wishlist source exists). */
  subscribed: boolean
  /** The wishlist source id when subscribed — needed to unsubscribe / toggle auto-sync. */
  sourceId: number | null
  /** The source's auto-sync flag when subscribed; null when not subscribed. */
  autoSync: boolean | null
}

export interface DiscoverTrack {
  deezerTrackId: string
  title: string
  artist: string
  album: string | null
  durationMs: number | null
  coverUrl: string | null
  inLibrary: boolean
  inWishlist: boolean
}

export interface DiscoverPlaylistDetail {
  playlist: DiscoverPlaylistSummary
  tracks: DiscoverTrack[]
}

export type DiscoverResolveProvider = "deezer" | "spotify"

export interface DiscoverResolveResult {
  provider: DiscoverResolveProvider
  playlistId: string
  title: string
  coverUrl: string | null
  trackCount: number
  subscribed: boolean
}

export async function fetchDiscoverGenres(): Promise<{ genres: DiscoverGenre[] }> {
  return requestJson<{ genres: DiscoverGenre[] }>("/api/discover/genres")
}

export async function fetchDiscoverPlaylists(
  params: { genreId?: number; search?: string; limit?: number } = {}
): Promise<{ playlists: DiscoverPlaylistSummary[] }> {
  const query = new URLSearchParams()
  if (params.genreId != null) query.set("genreId", String(params.genreId))
  if (params.search) query.set("search", params.search)
  if (params.limit != null) query.set("limit", String(params.limit))
  const qs = query.toString()
  return requestJson<{ playlists: DiscoverPlaylistSummary[] }>(
    `/api/discover/playlists${qs ? `?${qs}` : ""}`
  )
}

export async function fetchDiscoverPlaylist(id: string): Promise<DiscoverPlaylistDetail> {
  return requestJson<DiscoverPlaylistDetail>(`/api/discover/playlists/${encodeURIComponent(id)}`)
}

export async function resolveDiscoverUrl(url: string): Promise<DiscoverResolveResult> {
  return requestJson<DiscoverResolveResult>("/api/discover/resolve", {
    method: "POST",
    body: JSON.stringify({ url }),
  })
}

// ── Soulseek & library sync API ───────────────────────────────────────────────

export type SyncMode = "Off" | "Push" | "Receive"

/** Outbox rollup for Push mode — how many tracks sit in each sync state. */
export interface SyncOutboxCounts {
  pending: number
  uploading: number
  synced: number
  skippedRemoteBetter: number
  failed: number
}

export interface SyncStatus {
  mode: SyncMode
  receiveConfigured: boolean
  pushConfigured: boolean
  outbox: SyncOutboxCounts
}

export interface SoulseekStatus {
  configured: boolean
  connected: boolean
  version?: string | null
}

export type SoulseekUpgradeStatus =
  | "Queued"
  | "Searching"
  | "Downloading"
  | "AwaitingIngest"
  | "Completed"
  | "NotFound"
  | "Failed"
  | "Cancelled"

export interface SoulseekUpgrade {
  id: number
  songId: number
  songArtist?: string | null
  songTitle?: string | null
  songExtension?: string | null
  songBitrate?: number | null
  status: string
  candidateQualityScore?: number | null
  candidateInfoJson?: string | null
  error?: string | null
  createdAtUtc: string
  updatedAtUtc: string
  completedAtUtc?: string | null
}

/** Target one song by id, or a whole album by artist + album. */
export interface SoulseekUpgradeRequest {
  songId?: number
  artist?: string
  album?: string
}

export interface SoulseekUpgradeResult {
  queued: number
  skippedActive: number
}

/** Per-track sync state surfaced on the enrichment detail (Push deployments only). */
export interface TrackSyncView {
  status: "Pending" | "Uploading" | "Synced" | "SkippedRemoteBetter" | "Failed"
  attempts: number
  lastError?: string | null
  remoteQualityScore?: number | null
  updatedAtUtc: string
}

/** The song's latest Soulseek quality-upgrade attempt, surfaced on the enrichment detail. */
export interface SongUpgradeView {
  id: number
  status: SoulseekUpgradeStatus
  active: boolean
  candidateInfoJson?: string | null
  error?: string | null
  updatedAtUtc: string
}

export const sync = {
  getStatus(): Promise<SyncStatus> {
    return requestJson<SyncStatus>("/api/sync/status")
  },
}

export const soulseek = {
  getStatus(): Promise<SoulseekStatus> {
    return requestJson<SoulseekStatus>("/api/soulseek/status")
  },
  requestUpgrade(body: SoulseekUpgradeRequest): Promise<SoulseekUpgradeResult> {
    return requestJson<SoulseekUpgradeResult>("/api/soulseek/upgrades", {
      method: "POST",
      body: JSON.stringify(body),
    })
  },
  listUpgrades(status?: string, limit?: number): Promise<SoulseekUpgrade[]> {
    const params = new URLSearchParams()
    if (status) params.set("status", status)
    if (limit != null) params.set("limit", String(limit))
    const qs = params.toString()
    return requestJson<SoulseekUpgrade[]>(`/api/soulseek/upgrades${qs ? `?${qs}` : ""}`)
  },
  cancelUpgrade(id: number): Promise<{ cancelled: boolean }> {
    return requestJson<{ cancelled: boolean }>(`/api/soulseek/upgrades/${id}`, { method: "DELETE" })
  },
}

// ---------------------------------------------------------------------------
// Exported playlists API (Spotify Liked Songs + playlists → on-disk .m3u8)
// ---------------------------------------------------------------------------

export type ExportedPlaylistKind = "LikedSongs" | "Playlist"

// One of the owner's Spotify collections (Liked Songs or a playlist). Export is opt-in: `subscribed`
// is false until the owner adds it, at which point coverage fields (matched/filePath/lastGenerated)
// are populated by the export run. `id` is the subscription row id (null when not subscribed).
export interface PlaylistCollection {
  id: number | null
  kind: ExportedPlaylistKind
  spotifyPlaylistId?: string | null
  name: string
  imageUrl?: string | null
  ownerName?: string | null
  spotifyTrackTotal: number
  subscribed: boolean
  matchedTrackCount: number
  filePath?: string | null
  lastGeneratedAtUtc?: string | null
}

export interface PlaylistCollectionsResponse {
  spotifyConnected: boolean
  spotifyError?: string | null
  collections: PlaylistCollection[]
}

export async function fetchPlaylistCollections(): Promise<PlaylistCollectionsResponse> {
  return requestJson<PlaylistCollectionsResponse>("/api/playlists")
}

export async function subscribePlaylist(input: {
  kind: ExportedPlaylistKind
  spotifyPlaylistId?: string | null
  name: string
}): Promise<{ id: number; subscribed: boolean; queued: boolean }> {
  return requestJson("/api/playlists/subscribe", {
    method: "POST",
    body: JSON.stringify(input)
  })
}

export async function unsubscribePlaylist(id: number): Promise<{ message: string }> {
  return requestJson(`/api/playlists/${id}`, { method: "DELETE" })
}

export async function regenerateExportedPlaylists(): Promise<{ queued: boolean }> {
  return requestJson<{ queued: boolean }>("/api/playlists/regenerate", { method: "POST" })
}

// ---------------------------------------------------------------------------
// Auth API
// ---------------------------------------------------------------------------

export type AuthRole = "Owner" | "Demo" | "Friend"

export interface AuthMe {
  id: string
  email: string
  role: AuthRole
  displayName: string | null
}

export interface RequestLinkResult {
  ok: boolean
  /** Present only in dev / Console-fallback mode so devs can click without email. */
  magicLinkUrl?: string | null
  /** True when the server has no email service — the link was written to the server logs instead. */
  magicLinkInLogs?: boolean
}

export async function fetchCurrentUser(): Promise<AuthMe | null> {
  const response = await fetch(`${API_PREFIX}/api/auth/me`, { cache: "no-store" })
  if (response.status === 401) return null
  if (!response.ok) throw new Error(`auth/me failed: ${response.status}`)
  return (await response.json()) as AuthMe
}

/** Rename the signed-in account. Pass null to clear the name (UI falls back to the email). */
export async function updateDisplayName(displayName: string | null): Promise<AuthMe> {
  return requestJson<AuthMe>(`/api/auth/me`, {
    method: "PATCH",
    body: JSON.stringify({ displayName }),
  })
}

export async function requestMagicLink(email: string): Promise<RequestLinkResult> {
  const response = await fetch(`${API_PREFIX}/api/auth/request-link`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ email }),
    cache: "no-store",
  })
  if (response.status === 503) return { ok: false }
  const body = (await response.json().catch(() => ({}))) as {
    magicLinkUrl?: string | null
    magicLinkInLogs?: boolean
  }
  return { ok: true, magicLinkUrl: body.magicLinkUrl ?? null, magicLinkInLogs: body.magicLinkInLogs ?? false }
}

/** One signed-in account in this browser, as reported by the account switcher. */
export interface AccountView {
  userId: string
  email: string
  role: AuthRole
  displayName: string | null
  isActive: boolean
}

export interface SignOutResult {
  /** The parked account promoted to active by the logout, or null when none was left. */
  fallback: AccountView | null
}

export async function signOut(allSessions = false): Promise<SignOutResult> {
  const response = await fetch(`${API_PREFIX}/api/auth/logout${allSessions ? "?all=true" : ""}`, {
    method: "POST",
    cache: "no-store",
  })
  const body = (await response.json().catch(() => ({}))) as { fallback?: AccountView | null }
  return { fallback: body.fallback ?? null }
}

/** Accounts remembered in this browser: the active one first, then parked ones by recency. */
export async function listAccounts(): Promise<AccountView[]> {
  const response = await fetch(`${API_PREFIX}/api/auth/accounts`, { cache: "no-store" })
  if (!response.ok) throw new Error(`auth/accounts failed: ${response.status}`)
  const body = (await response.json()) as { accounts: AccountView[] }
  return body.accounts
}

/**
 * Makes a parked account the active one. Returns null when the account is no longer switchable
 * (its parked session expired or was revoked) — the server prunes it in that case.
 */
export async function switchAccount(userId: string): Promise<AccountView | null> {
  const response = await fetch(`${API_PREFIX}/api/auth/switch`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ userId }),
    cache: "no-store",
  })
  if (response.status === 404) return null
  if (!response.ok) throw new Error(`auth/switch failed: ${response.status}`)
  const body = (await response.json()) as { user: AccountView }
  return body.user
}

/** A bearer token minted for a native client (Android app), plus when it stops working. */
export interface DeviceTokenResult {
  accessToken: string
  tokenType: string
  expiresAtUtc: string
}

/**
 * Mints a bearer token for a native client from the current browser session. The token rides its
 * own server-side session row, so signing the browser out does not unpair the phone — "Sign out
 * everywhere" does.
 */
export async function createDeviceToken(): Promise<DeviceTokenResult> {
  return requestJson<DeviceTokenResult>("/api/auth/device-token", { method: "POST" })
}

export async function signInAsDemo(): Promise<void> {
  const response = await fetch(`${API_PREFIX}/api/auth/demo-login`, {
    method: "POST",
    cache: "no-store",
  })
  if (!response.ok) throw new Error(`demo-login failed: ${response.status}`)
}

// ---------------------------------------------------------------------------
// Passkey (WebAuthn) API
// ---------------------------------------------------------------------------

export interface PasskeyView {
  id: string
  displayName: string
  aaGuid: string
  createdAtUtc: string
  lastUsedAtUtc: string | null
}

const WEBAUTHN_PREFIX = `${API_PREFIX}/api/auth/webauthn`

/** Enrolls a new passkey for the signed-in owner. Runs the full begin → ceremony → complete flow. */
export async function registerPasskey(displayName: string): Promise<PasskeyView> {
  const beginRes = await fetch(`${WEBAUTHN_PREFIX}/register/begin`, { method: "POST", cache: "no-store" })
  if (!beginRes.ok) throw new Error(`Could not start passkey registration (${beginRes.status}).`)
  const options = (await beginRes.json()) as Record<string, unknown>

  const attestation = await createPasskey(options)

  const completeRes = await fetch(`${WEBAUTHN_PREFIX}/register/complete`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ attestation, displayName }),
    cache: "no-store",
  })
  if (!completeRes.ok) {
    const body = (await completeRes.json().catch(() => ({}))) as { error?: string }
    throw new Error(body.error === "verification_failed" ? "Passkey could not be verified." : "Passkey registration failed.")
  }
  return (await completeRes.json()) as PasskeyView
}

/** Signs in with an existing passkey. On success the session cookie is set; caller should navigate. */
export async function loginWithPasskey(): Promise<void> {
  const beginRes = await fetch(`${WEBAUTHN_PREFIX}/authenticate/begin`, { method: "POST", cache: "no-store" })
  if (!beginRes.ok) throw new Error(`Could not start passkey sign-in (${beginRes.status}).`)
  const options = (await beginRes.json()) as Record<string, unknown>

  const assertion = await getPasskeyAssertion(options)

  const completeRes = await fetch(`${WEBAUTHN_PREFIX}/authenticate/complete`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(assertion),
    cache: "no-store",
  })
  if (!completeRes.ok) throw new Error("Passkey sign-in failed.")
}

export async function listPasskeys(): Promise<PasskeyView[]> {
  const response = await fetch(`${WEBAUTHN_PREFIX}/credentials`, { cache: "no-store" })
  if (!response.ok) throw new Error(`Could not load passkeys (${response.status}).`)
  return (await response.json()) as PasskeyView[]
}

export async function deletePasskey(id: string): Promise<void> {
  const response = await fetch(`${WEBAUTHN_PREFIX}/credentials/${encodeURIComponent(id)}`, {
    method: "DELETE",
    cache: "no-store",
  })
  if (!response.ok) throw new Error(`Could not remove passkey (${response.status}).`)
}

// ---------------------------------------------------------------------------
// Settings API
// ---------------------------------------------------------------------------

export interface SettingsPathsView {
  sourceDirectory: string
  destinationDirectory: string
  fpcalcPath: string
}

export interface SettingsProvidersView {
  acoustId: boolean
  musicBrainzWeb: boolean
  spotifyApi: boolean
  tracker: boolean
  deezer: boolean
  appleMusic: boolean
}

export interface SettingsSpotifyView {
  oAuthRedirectBaseUrl: string
  scopes: string[]
}

export interface SettingsQualityGradingView {
  enabled: boolean
  configured: boolean
}

export interface SettingsLyricsTranscriptionView {
  /** True when a transcription provider is configured on the server (the experimental feature is live). */
  enabled: boolean
}

export interface SettingsLyricsTranslationView {
  /** True when the OpenRouter creds (QualityGrading) + a translation model are configured on the server. */
  enabled: boolean
}

export interface SettingsDownloadsView {
  /** Deploy-time feature switch: wishlist downloads available at all (yt-dlp + a writable download dir). */
  enabled: boolean
  /** Runtime toggle: auto-sweep Pending wishlist items in the background. Flippable from the UI. */
  autoDownload: boolean
  /**
   * Runtime toggle: queue the missing tracks of albums you already own part of. Rides the same
   * downloader, so it only means anything when {@link enabled} is true.
   */
  albumCompletion: boolean
}

export interface SettingsResponse {
  paths: SettingsPathsView
  providers: SettingsProvidersView
  spotify: SettingsSpotifyView
  qualityGrading: SettingsQualityGradingView
  lyricsTranscription: SettingsLyricsTranscriptionView
  lyricsTranslation?: SettingsLyricsTranslationView
  downloads: SettingsDownloadsView
  updatedAtUtc: string | null
}

export interface SettingsUpdateRequest {
  providers?: Partial<SettingsProvidersView>
  qualityGrading?: { enabled?: boolean }
  downloads?: { autoDownload?: boolean; albumCompletion?: boolean }
}

export async function fetchSettings(): Promise<SettingsResponse> {
  return requestJson<SettingsResponse>("/api/settings")
}

export async function updateSettings(update: SettingsUpdateRequest): Promise<void> {
  await requestJson<unknown>("/api/settings", {
    method: "PUT",
    body: JSON.stringify(update),
  })
}

export async function fetchReviewTracks(): Promise<ApiSong[]> {
  const result = await requestJson<SongsResponse>(
    "/songs?enrichmentStatus=needsreview"
  )
  return result.songs ?? []
}

/** Enrichment-review queue filter: songs still needing a human, or already matched. */
export type ReviewQueueFilter = "needsreview" | "matched"

/** Fetch songs for the Provenance & review queue by enrichment status. */
export async function fetchReviewQueue(filter: ReviewQueueFilter): Promise<ApiSong[]> {
  const result = await requestJson<SongsResponse>(
    `/songs?enrichmentStatus=${filter}`
  )
  return result.songs ?? []
}

// ── Duplicates (acoustic + metadata clusters) ──────────────────────────────────

/** How confident the server is that a member/group really is the same recording. */
export type DuplicateConfidence = "confirmed" | "suspected"

/** One file inside a duplicate cluster, with its match evidence. */
export interface DuplicateMember {
  id: number
  sourcePath: string
  fileName: string
  extension?: string | null
  fileSizeBytes: number
  artist?: string | null
  albumArtist?: string | null
  album?: string | null
  title?: string | null
  year?: number | null
  trackNumber?: number | null
  durationSeconds?: number | null
  bitrate?: number | null
  fingerprint?: string | null
  isDuplicate: boolean
  duplicateOfId?: number | null
  enrichmentStatus?: string | number | null
  destinationPath?: string | null
  /** True when this copy was already built to the destination library. */
  isBuilt: boolean
  /** True for the server-elected keeper of the cluster. */
  isKeeper: boolean
  /** True when the user manually pinned this copy as the keeper. */
  isPinned: boolean
  confidence: DuplicateConfidence
  /** Match evidence: exact-fingerprint | fingerprint-similarity | acoustid | isrc | metadata. */
  reasons: string[]
  /** Chromaprint similarity in [0,1] when the audio was compared; null otherwise. */
  similarity?: number | null
  /** Server-computed keep-priority (codec tier dominates, bitrate breaks ties). */
  qualityScore: number
}

export interface DuplicateGroup {
  /** Stable cluster id (the lowest song id in the cluster). */
  groupId: number
  confidence: DuplicateConfidence
  keeper: DuplicateMember
  /** All cluster members, keeper first (server-ranked). */
  members: DuplicateMember[]
}

export interface DuplicatesResponse {
  totalDuplicates: number
  groups: number
  duplicateGroups: DuplicateGroup[]
}

/** Duplicate clusters (confirmed + suspected) with keeper election and per-member evidence. */
export async function fetchDuplicates(): Promise<DuplicatesResponse> {
  return requestJson<DuplicatesResponse>("/api/library/duplicates")
}

/** Re-run duplicate detection now (it also runs after every fingerprint pass). */
export async function detectDuplicates(): Promise<void> {
  await requestJson<unknown>("/api/library/duplicates/detect", { method: "POST" })
}

/** Choose the keeper of a cluster; the choice is pinned across detection re-runs. */
export async function resolveDuplicates(keeperId: number, loserIds: number[]): Promise<void> {
  await requestJson<unknown>("/api/library/duplicates/resolve", {
    method: "POST",
    body: JSON.stringify({ keeperId, loserIds }),
  })
}

/** Mark songs as NOT duplicates of each other; persists across detection re-runs. */
export async function dismissDuplicates(songIds: number[]): Promise<void> {
  await requestJson<unknown>("/api/library/duplicates/dismiss", {
    method: "POST",
    body: JSON.stringify({ songIds }),
  })
}

// ── Artist dedup (variant spellings + combined credits) ────────────────────────

export interface ArtistNameStat {
  name: string
  songCount: number
  musicBrainzIds: string[]
}

export interface ArtistDuplicateCluster {
  suggestedCanonical: string
  variants: ArtistNameStat[]
  /** Why these clustered: "same name after normalization" | "same MusicBrainz artist id" | "similar spelling". */
  evidence: string[]
}

export interface CombinedCreditCandidate {
  credit: string
  parts: string[]
  songCount: number
}

export interface ArtistDuplicateReport {
  clusters: ArtistDuplicateCluster[]
  combinedCredits: CombinedCreditCandidate[]
}

export interface ArtistMergeResult {
  songsUpdated: number
  songsRequeued: number
  aliasesStored: number
}

/** Clusters of artist-name spellings that likely refer to the same artist. */
export async function fetchArtistDuplicates(): Promise<ArtistDuplicateReport> {
  return requestJson<ArtistDuplicateReport>("/api/library/artists/duplicates")
}

/** Merge variant spellings onto a canonical artist name (rewrites tags, re-queues built files). */
export async function mergeArtists(canonicalName: string, variantNames: string[]): Promise<ArtistMergeResult> {
  return requestJson<ArtistMergeResult>("/api/library/artists/merge", {
    method: "POST",
    body: JSON.stringify({ canonicalName, variantNames }),
  })
}

/** Backfill the discrete Artists list for songs whose display credit is a combined "A & B" string. */
export async function splitArtistCredit(creditName: string): Promise<{ songsUpdated: number; songsRequeued: number }> {
  return requestJson("/api/library/artists/split-credit", {
    method: "POST",
    body: JSON.stringify({ creditName }),
  })
}

/** Mark artist-name spellings as NOT the same artist; persists across detections. */
export async function dismissArtistDuplicates(names: string[]): Promise<void> {
  await requestJson<unknown>("/api/library/artists/dismiss", {
    method: "POST",
    body: JSON.stringify({ names }),
  })
}

// ── Album dedup (split groups + near-duplicate pairs) ──────────────────────────

export interface AlbumSplitGroup {
  artistKey: string
  albumKey: string
  memberCount: number
  membersNeedingCorrection: number
  distinctReleaseIds: string[]
  distinctFolders: string[]
  electedIdentity: {
    album?: string | null
    albumArtist?: string | null
    year?: number | null
    musicBrainzReleaseId?: string | null
  }
}

export interface AlbumDuplicatePair {
  artistKey: string
  artistDisplay: string
  albumA: string
  songCountA: number
  albumB: string
  songCountB: number
  fuzzyRatio?: number | null
  /** "same title after normalization" | "similar title" */
  evidence: string
}

/** Dry-run report of split albums (tracks of one logical album disagreeing on identity). */
export async function fetchSplitAlbums(): Promise<{ count: number; groups: AlbumSplitGroup[] }> {
  return requestJson("/api/enrichment/split-albums")
}

/** Run the split-album self-heal now (same pass the builder runs when idle). */
export async function healSplitAlbums(): Promise<{ groupsHealed: number; songsCorrected: number; songsRequeued: number }> {
  return requestJson("/api/enrichment/split-albums/heal", { method: "POST" })
}

/** Near-duplicate album pairs the exact grouping key misses ("The Blueprint 3" vs "Blueprint 3"). */
export async function fetchAlbumDuplicates(): Promise<{ count: number; pairs: AlbumDuplicatePair[] }> {
  return requestJson("/api/library/albums/duplicates")
}

/** Rewrite mergeAlbum's spelling onto keepAlbum so both halves share one grouping key. */
export async function mergeAlbums(artist: string, keepAlbum: string, mergeAlbum: string): Promise<void> {
  await requestJson<unknown>("/api/library/albums/merge", {
    method: "POST",
    body: JSON.stringify({ artist, keepAlbum, mergeAlbum }),
  })
}

/** Mark two album titles as NOT the same album; persists across detections. */
export async function dismissAlbumDuplicates(artist: string, albumA: string, albumB: string): Promise<void> {
  await requestJson<unknown>("/api/library/albums/dismiss", {
    method: "POST",
    body: JSON.stringify({ artist, albumA, albumB }),
  })
}

// ── Dedup action history (merges / splits / heals, with revert) ────────────────

export interface DedupAction {
  /** artist-merge | album-merge | artist-credit-split | album-identity-heal */
  source: string
  createdAtUtc: string
  /** Stable batch id for revert calls. */
  batchTicks: number
  songCount: number
  changeCount: number
  /** Human labels like `AlbumArtist → "Kanye West" (348 songs)`. */
  highlights: string[]
  reverted: boolean
  revertible: boolean
}

/** Recent dedup actions, newest first (reconstructed from the metadata-change audit log). */
export async function fetchDedupActions(): Promise<{ count: number; actions: DedupAction[] }> {
  return requestJson("/api/library/dedup/actions")
}

/** Revert one dedup action: restores old values and re-queues built files for re-tag. */
export async function revertDedupAction(source: string, batchTicks: number): Promise<void> {
  await requestJson<unknown>("/api/library/dedup/actions/revert", {
    method: "POST",
    body: JSON.stringify({ source, batchTicks }),
  })
}

// ── Enrichment detail (candidate matches) ──────────────────────────────────────

export interface EnrichmentCandidate {
  title?: string | null
  artist?: string | null
  albumArtist?: string | null
  album?: string | null
  year?: number | null
  trackNumber?: number | null
  isrc?: string | null
  musicBrainzId?: string | null
  musicBrainzReleaseId?: string | null
  spotifyId?: string | null
  acoustIdTrackId?: string | null
  matchedBy?: string | null
  matchConfidence?: number | null
  matchWarnings?: string[] | null
  recommendedStatus?: string | null
}

export interface ProviderAttempt {
  provider: string
  status: string
  attemptedAtUtc: string
  retryAfterUtc?: string | null
  nextRetryAfterUtc?: string | null
  error?: string | null
  /** The term the provider searched (resolved artist/title, or tracker title-only). Null for AcoustID. */
  searchQuery?: string | null
  candidate: EnrichmentCandidate | null
}

/** One changed field in the before→after diff. `source`/`current` are scalars. */
export interface MetadataDiffEntry {
  field: string
  source?: string | number | null
  current?: string | number | null
}

/** A field-level entry from the song's metadata change history (powers the timeline). */
export interface ChangeLogEntry {
  id: number
  field: string
  oldValue?: string | null
  newValue?: string | null
  source?: string | null
  confidence?: number | null
  createdAtUtc: string
  appliedAtUtc?: string | null
  revertedAtUtc?: string | null
  applied: boolean
  proposed: boolean
}

export interface EnrichmentDetail {
  id: number
  fileName: string
  sourcePath: string
  destinationPath?: string | null
  enrichmentStatus: string
  isManuallyApproved?: boolean
  manuallyApprovedAtUtc?: string | null
  matchedBy?: string | null
  matchConfidence?: number | null
  matchWarnings?: string[] | null
  enrichmentError?: string | null
  originalMetadataCaptured: boolean
  source: EnrichmentCandidate | null
  current: EnrichmentCandidate
  diff: MetadataDiffEntry[]
  providerAttempts: ProviderAttempt[]
  changeLog: ChangeLogEntry[]
  /** Push-mode sync state for this track; null when sync is off or the track has no outbox row. */
  trackSync?: TrackSyncView | null
  /** Latest Soulseek quality-upgrade attempt; null when none has been requested. */
  upgrade?: SongUpgradeView | null
}

export async function fetchEnrichmentDetail(songId: number): Promise<EnrichmentDetail> {
  return requestJson<EnrichmentDetail>(`/songs/${songId}/enrichment-detail`)
}

/* ------------------------------------------------------------------ *
 * AI quality grading
 * ------------------------------------------------------------------ */

export type QualityVerdict = "Excellent" | "Good" | "Questionable" | "Wrong" | "Ungradeable"

export interface QualityIssue {
  code: string
  severity: string
  detail?: string | null
}

export interface QualityVerdictCounts {
  excellent: number
  good: number
  questionable: number
  wrong: number
  ungradeable: number
}

export interface QualityRollupView {
  graded: number
  averageScore: number | null
  verdicts: QualityVerdictCounts
  topIssues: { code: string; count: number }[]
}

export interface QualityWorstOffender {
  songId: number
  fileName: string
  sourcePath: string
  artist?: string | null
  title?: string | null
  album?: string | null
  score: number
  verdict: QualityVerdict
  summary?: string | null
  issues: QualityIssue[]
  enrichmentStatusAtGrade?: string | null
  destinationPathPreview?: string | null
  gradedAtUtc: string
}

export interface QualityDirectoryRollup {
  directory: string
  rollup: QualityRollupView
  worstVerdict: QualityVerdict
  wrongCount: number
}

export interface QualityOverview {
  gradeableTotal: number
  coverage: number
  library: QualityRollupView
  /** Auto-asked-for-review (NeedsReview at grade time). */
  flaggedCount: number
  /** Auto-accepted (Matched) but graded Wrong/Questionable — the algorithm's blind spots. */
  silentFailureCount: number
  /** Auto-accepted (Matched) and graded Excellent. */
  verifiedCleanCount: number
  /** Graded songs whose prompt version or model changed since — surfaced, not auto-regraded. */
  outdatedCount: number
  worstOffenders: QualityWorstOffender[]
  directories: QualityDirectoryRollup[]
}

/** Which workbench bucket a graded song falls into (mirrors the backend classifier). */
export type QualityBucketName = "flagged" | "silent" | "verified" | "other"

/** A graded song row for the AI-quality workbench master list. */
export interface QualitySongRow {
  songId: number
  fileName: string
  sourcePath: string
  artist?: string | null
  title?: string | null
  album?: string | null
  score: number
  verdict: QualityVerdict
  summary?: string | null
  issues: QualityIssue[]
  enrichmentStatusAtGrade?: string | null
  destinationPathPreview?: string | null
  gradedAtUtc: string
  bucket: QualityBucketName
  isOutdated?: boolean
}

/** Filter category for the workbench list: a bucket, a verdict, or "all". */
export type QualityCategory =
  | "all"
  | "flagged"
  | "silent"
  | "verified"
  | "wrong"
  | "questionable"
  | "good"
  | "excellent"
  | "ungradeable"

export interface QualitySongsPage {
  total: number
  skip: number
  take: number
  items: QualitySongRow[]
}

export async function fetchQualitySongs(
  category: QualityCategory = "all",
  skip = 0,
  take = 200
): Promise<QualitySongsPage> {
  return requestJson<QualitySongsPage>(
    `/api/quality/songs?category=${encodeURIComponent(category)}&skip=${skip}&take=${take}`
  )
}

export interface SongQualityGradeView {
  songId: number
  graded: boolean
  score?: number
  verdict?: QualityVerdict
  summary?: string | null
  issues?: QualityIssue[]
  model?: string | null
  promptVersion?: number
  enrichmentStatusAtGrade?: string | null
  destinationPathPreview?: string | null
  durationMs?: number | null
  gradedAtUtc?: string
  isOutdated?: boolean
  historyCount?: number
}

export interface QualityGradeResult {
  songId: number
  outcome: string
  score?: number | null
  verdict?: QualityVerdict | null
  summary?: string | null
  issues: QualityIssue[]
  model?: string | null
  destinationPathPreview?: string | null
  durationMs?: number | null
  gradedAtUtc?: string | null
}

export interface QualityProgress {
  active: boolean
  aiGradingConfigured?: boolean
  aiGradingEnabled?: boolean
  lastError?: { code: string; message: string; atUtc: string } | null
  runId?: string
  total?: number
  processed?: number
  graded?: number
  skipped?: number
  failed?: number
  isComplete?: boolean
  startedAt?: string
  completedAt?: string | null
}

export async function fetchQualityOverview(): Promise<QualityOverview> {
  return requestJson<QualityOverview>("/api/quality/overview")
}

export async function fetchSongQualityGrade(songId: number): Promise<SongQualityGradeView> {
  return requestJson<SongQualityGradeView>(`/api/quality/songs/${songId}`)
}

export async function gradeSong(songId: number): Promise<QualityGradeResult> {
  return requestJson<QualityGradeResult>(`/api/quality/songs/${songId}/grade`, { method: "POST" })
}

export async function gradeOutdatedSongs(): Promise<{ enqueued: number }> {
  return requestJson<{ enqueued: number }>("/api/quality/grade-outdated", { method: "POST" })
}

export async function gradeAllSongs(): Promise<{ enqueued: number }> {
  return requestJson<{ enqueued: number }>("/api/quality/grade-all", { method: "POST" })
}

export async function gradeDirectory(path: string): Promise<{ enqueued: number; path: string }> {
  return requestJson<{ enqueued: number; path: string }>("/api/quality/grade-directory", {
    method: "POST",
    body: JSON.stringify({ path }),
  })
}

export async function fetchQualityProgress(): Promise<QualityProgress> {
  return requestJson<QualityProgress>("/api/quality/progress")
}

/** Fetches a single song's grading dossier and copies the pretty-printed JSON to the clipboard. */
export async function copyQualitySongDossier(songId: number): Promise<void> {
  const textPromise = requestJson<unknown>(`/api/quality/export/songs/${songId}`).then((data) =>
    JSON.stringify(data, null, 2),
  )

  // Hand the clipboard a *promise* of the data so the write is initiated
  // synchronously inside the click's user-activation window. Awaiting the
  // fetch first lets focus/activation lapse, which throws "Document is not
  // focused" and can also stall. Fall back to writeText where ClipboardItem
  // promises aren't supported.
  if (typeof ClipboardItem !== "undefined" && "write" in navigator.clipboard) {
    const blob = textPromise.then((text) => new Blob([text], { type: "text/plain" }))
    await navigator.clipboard.write([new ClipboardItem({ "text/plain": blob })])
  } else {
    await navigator.clipboard.writeText(await textPromise)
  }
}

// --- Pipeline performance snapshots (the version timeline) ---

export interface SnapshotAiBreakdown {
  excellent: number
  good: number
  questionable: number
  wrong: number
  ungradeable: number
}

export interface SnapshotSummary {
  id: number
  capturedAtUtc: string
  trigger: string
  triggerLabel?: string | null
  version?: string | null
  configHash: string
  totalSongs: number
  matched: number
  needsReview: number
  failed: number
  pending: number
  duplicates: number
  buildDone: number
  matchRate?: number | null
  avgMatchConfidence?: number | null
  providerMatched?: Record<string, number> | null
  graded: number
  avgAiScore?: number | null
  ai: SnapshotAiBreakdown
}

export interface SnapshotConfigDiffEntry {
  key: string
  from?: string | null
  to?: string | null
}

export interface SnapshotDetail {
  summary: SnapshotSummary
  config: unknown
  previousSnapshotId?: number | null
  configDiff: SnapshotConfigDiffEntry[]
}

export interface SnapshotSongState {
  status: string
  confidence?: number | null
  matchedBy?: string | null
  aiScore?: number | null
  aiVerdict?: string | null
}

export interface SnapshotSongDiff {
  songId: number
  artist?: string | null
  title?: string | null
  sourcePath?: string | null
  fileName?: string | null
  reasons: string[]
  from: SnapshotSongState
  to: SnapshotSongState
}

export interface SnapshotCompare {
  from: SnapshotSummary
  to: SnapshotSummary
  comparedSongs: number
  regressedCount: number
  improvedCount: number
  regressed: SnapshotSongDiff[]
  improved: SnapshotSongDiff[]
}

export async function fetchSnapshots(): Promise<SnapshotSummary[]> {
  return requestJson<SnapshotSummary[]>("/api/snapshots")
}

export async function fetchSnapshot(id: number): Promise<SnapshotDetail> {
  return requestJson<SnapshotDetail>(`/api/snapshots/${id}`)
}

export async function fetchSnapshotCompare(from?: number, to?: number): Promise<SnapshotCompare> {
  const params = new URLSearchParams()
  if (from != null) params.set("from", String(from))
  if (to != null) params.set("to", String(to))
  const query = params.toString()
  return requestJson<SnapshotCompare>(`/api/snapshots/compare${query ? `?${query}` : ""}`)
}

export async function captureSnapshot(
  label?: string,
): Promise<{ captured: boolean; reason?: string; snapshot?: SnapshotSummary }> {
  return requestJson("/api/snapshots", {
    method: "POST",
    body: JSON.stringify({ label: label ?? null }),
  })
}

// --- Library history (destination-write change feed) ---

export interface HistoryRawChange {
  songId?: number | null
  trackTitle?: string | null
  field: string
  oldValue?: string | null
  newValue?: string | null
  isAlbumIdentity: boolean
  writtenAtUtc: string
}

export interface HistorySummary {
  id: string
  /** "consolidation" | "artist-rename" | "year-correction" | "cover" | "tags" */
  kind: string
  headline: string
  albumArtist?: string | null
  album?: string | null
  trackCount: number
  latestWrittenAtUtc: string
  runId?: string | null
  changes: HistoryRawChange[]
}

export interface HistoryFeedResponse {
  summaries: HistorySummary[]
  nextCursor?: string | null
  totalEventsInWindow: number
}

export async function fetchHistory(params: {
  from?: string
  to?: string
  artist?: string
  album?: string
  cursor?: string
  take?: number
} = {}): Promise<HistoryFeedResponse> {
  const q = new URLSearchParams()
  if (params.from) q.set("from", params.from)
  if (params.to) q.set("to", params.to)
  if (params.artist) q.set("artist", params.artist)
  if (params.album) q.set("album", params.album)
  if (params.cursor) q.set("cursor", params.cursor)
  if (params.take != null) q.set("take", String(params.take))
  const query = q.toString()
  return requestJson<HistoryFeedResponse>(`/api/history${query ? `?${query}` : ""}`)
}
