import { createPasskey, getPasskeyAssertion } from "$lib/webauthn-client"
import type { PlayerSong } from "$lib/stores/player.svelte"

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
  /** When the track was copied/tagged into the destination library; null until built. */
  libraryBuiltAtUtc?: string | null
  /** Pipeline build state: Pending/Copied/Tagged/Done/Failed (number or string). */
  libraryBuildStatus?: string | number | null
  matchedBy?: string | null
  matchConfidence?: number | null
  matchWarnings?: string[] | null
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
}

interface SongsResponse {
  count: number
  includeDeleted: boolean
  songs: ApiSong[]
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
      // Other error codes (e.g. "owner_required") read as raw JSON in error banners —
      // fall back to the code itself as readable text.
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

/** Aggregated view of all songs sharing an `(albumArtist, album)` pair. */
export interface AlbumSummary {
  /**
   * Stable key. For built songs this is the **destination folder directory** — the same unit
   * Navidrome groups on (one reconciled MUSICBRAINZ_ALBUMID is written per folder), so a single
   * album name that was split across releases/folders shows as the same separate cards the player
   * does. Songs without a destination path fall back to `${artistLower}::${titleLower}`.
   * Used as the `?album=` URL param and for client-side album lookup.
   */
  key: string
  title: string
  artist: string
  year: number | null
  trackCount: number
  /** Sum of durationSeconds across known tracks. */
  durationSeconds: number
  /** Sum of fileSizeBytes across known tracks. */
  byteSize: number
  /** First non-null genre encountered; null otherwise. */
  genre: string | null
  /** First non-null record label encountered. */
  label: string | null
  /** First non-null catalog number encountered. */
  catalogNumber: string | null
  /** First non-null barcode / UPC encountered. */
  upc: string | null
  /** First non-null full release date (ISO string) encountered. */
  releaseDate: string | null
  /** First non-null musicBrainzReleaseId encountered. */
  musicBrainzReleaseId: string | null
  /** First non-null albumArt URL encountered. */
  coverUrl: string | null
  /** Most recent "added" time across the album's tracks (ISO string); null if none known. */
  addedAtUtc: string | null
  /** Songs ordered by track number then title. */
  songs: ApiSong[]
}

function nonEmpty(value: string | null | undefined): string | null {
  const trimmed = (value ?? "").trim()
  return trimmed.length > 0 ? trimmed : null
}

/**
 * Destination folder directory of a built song — the album folder the music server reads, where
 * the library builder elects one reconciled release identity. Null when the song isn't built yet.
 * Mirrors the derivation in AlbumPage.svelte.
 */
function destinationFolderOf(song: ApiSong): string | null {
  const path = nonEmpty(song.destinationPath)
  if (!path) return null
  const idx = path.lastIndexOf("/")
  return idx > 0 ? path.slice(0, idx) : path
}

/** Effective "added to library" time for a song: build time, falling back to index time. */
export function songAddedTime(s: ApiSong): number {
  const t = s.libraryBuiltAtUtc ?? s.indexedAtUtc
  return t ? new Date(t).getTime() : 0
}

/** Sort albums newest-first by their `addedAtUtc`. Returns a new array. */
export function sortAlbumsByRecency(albums: AlbumSummary[]): AlbumSummary[] {
  return [...albums].sort(
    (a, b) =>
      new Date(b.addedAtUtc ?? 0).getTime() - new Date(a.addedAtUtc ?? 0).getTime(),
  )
}

/**
 * Group raw songs into album summaries used by Gallery / AlbumPage.
 * Built songs group by their destination folder (the unit the music server reads, so the app
 * mirrors how the player splits one album name across releases); unbuilt songs fall back to
 * `artistLower::titleLower` so search/command-palette call sites keep working.
 */
export function buildAlbumsFromSongs(songs: ApiSong[]): AlbumSummary[] {
  const map = new Map<string, AlbumSummary>()
  for (const song of songs) {
    const title = nonEmpty(song.album) ?? UNKNOWN_ALBUM
    const artist = nonEmpty(song.albumArtist) ?? nonEmpty(song.artist) ?? UNKNOWN_ARTIST
    const key = destinationFolderOf(song) ?? `${artist.toLowerCase()}::${title.toLowerCase()}`
    let entry = map.get(key)
    if (!entry) {
      entry = {
        key,
        title,
        artist,
        year: song.year ?? null,
        trackCount: 0,
        durationSeconds: 0,
        byteSize: 0,
        genre: null,
        label: null,
        catalogNumber: null,
        upc: null,
        releaseDate: null,
        musicBrainzReleaseId: null,
        coverUrl: null,
        addedAtUtc: null,
        songs: [],
      }
      map.set(key, entry)
    }
    entry.trackCount += 1
    const added = song.libraryBuiltAtUtc ?? song.indexedAtUtc
    if (added && (!entry.addedAtUtc || added > entry.addedAtUtc)) entry.addedAtUtc = added
    entry.durationSeconds += song.durationSeconds ?? 0
    entry.byteSize += song.fileSizeBytes ?? 0
    if (song.year && (!entry.year || song.year < entry.year)) entry.year = song.year
    entry.genre ??= nonEmpty(song.genre)
    entry.label ??= nonEmpty(song.label)
    entry.catalogNumber ??= nonEmpty(song.catalogNumber)
    entry.upc ??= nonEmpty(song.upc)
    entry.releaseDate ??= nonEmpty(song.releaseDate)
    if (!entry.musicBrainzReleaseId && song.musicBrainzReleaseId) {
      entry.musicBrainzReleaseId = song.musicBrainzReleaseId
    }
    if (!entry.coverUrl) entry.coverUrl = coverUrlForSong(song)
    entry.songs.push(song)
  }
  for (const album of map.values()) {
    album.songs.sort((a, b) => {
      const na = a.trackNumber ?? Number.POSITIVE_INFINITY
      const nb = b.trackNumber ?? Number.POSITIVE_INFINITY
      if (na !== nb) return na - nb
      const ta = (a.title ?? a.fileName).toLocaleLowerCase()
      const tb = (b.title ?? b.fileName).toLocaleLowerCase()
      return ta.localeCompare(tb)
    })
  }
  return Array.from(map.values()).sort((a, b) => {
    const artistCmp = a.artist.localeCompare(b.artist)
    if (artistCmp !== 0) return artistCmp
    return a.title.localeCompare(b.title)
  })
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
 * Stable `(artist, album)` key for a song — `${artistLower}::${titleLower}`,
 * matching the keys produced by {@link buildAlbumsFromSongs}. Use it to build
 * `/library?album=<key>` deep-links from a single song.
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
  const result = await requestJson<SongsResponse>(`/songs?includeDeleted=${includeDeleted}`)
  return result.songs ?? []
}

// ── Canonical album tracklist (multi-provider, reconciled; full-album view) ─────

/** One reconciled canonical track. `ownedSongId` is null when the user is missing it. */
export interface CanonicalTrack {
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
}

export async function fetchTrackLyrics(trackId: number): Promise<TrackLyricsResponse> {
  return requestJson<TrackLyricsResponse>(`/api/tracks/${trackId}/lyrics`)
}

/** Choose which lyrics the synced viewer shows when both an LRCLIB version and an AI transcription exist. */
export async function setPreferredLyricsSource(
  songId: number,
  source: "lrclib" | "transcribed"
): Promise<{ id: number; preferredLyricsSource: string }> {
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
}

/**
 * Experimental: transcribe a song's audio via OpenAI Whisper into a synced LRC. The result is stored
 * separately from the LRCLIB lyrics (for side-by-side comparison) and never re-tags the file on disk.
 */
export async function transcribeSongLyrics(songId: number): Promise<TranscribeLyricsResponse> {
  return requestJson<TranscribeLyricsResponse>(`/songs/${songId}/lyrics/transcribe`, { method: "POST" })
}

export function getSongStreamUrl(songId: number): string {
  return `${API_PREFIX}/songs/${songId}/stream`
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

// ── Likes + play reporting ───────────────────────────────────────────────────

/** Mark a song as liked. Idempotent — re-liking keeps the original timestamp. */
export async function likeSong(songId: number): Promise<{ id: number; likedAtUtc: string }> {
  return requestJson<{ id: number; likedAtUtc: string }>(`/songs/${songId}/like`, { method: "POST" })
}

export async function unlikeSong(songId: number): Promise<{ id: number; likedAtUtc: null }> {
  return requestJson<{ id: number; likedAtUtc: null }>(`/songs/${songId}/like`, { method: "DELETE" })
}

/**
 * Record a playback start (bumps play count + last-played). Fire-and-forget from the player;
 * demo sessions are write-blocked server-side, so callers should swallow failures.
 */
export async function reportSongPlayed(
  songId: number
): Promise<{ id: number; playCount: number; lastPlayedAtUtc: string }> {
  return requestJson<{ id: number; playCount: number; lastPlayedAtUtc: string }>(
    `/songs/${songId}/played`,
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
}

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

export async function fetchWishlist(status?: WishlistItemStatus, offset = 0, limit = 50): Promise<WishlistItemsResponse> {
  const params = new URLSearchParams({ offset: String(offset), limit: String(limit) })
  if (status) params.set("status", status)
  return requestJson<WishlistItemsResponse>(`/api/wishlist?${params.toString()}`)
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

export type AuthRole = "Owner" | "Demo"

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
}

export async function fetchCurrentUser(): Promise<AuthMe | null> {
  const response = await fetch(`${API_PREFIX}/api/auth/me`, { cache: "no-store" })
  if (response.status === 401) return null
  if (!response.ok) throw new Error(`auth/me failed: ${response.status}`)
  return (await response.json()) as AuthMe
}

export async function requestMagicLink(email: string): Promise<RequestLinkResult> {
  const response = await fetch(`${API_PREFIX}/api/auth/request-link`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ email }),
    cache: "no-store",
  })
  if (response.status === 503) return { ok: false }
  const body = (await response.json().catch(() => ({}))) as { magicLinkUrl?: string | null }
  return { ok: true, magicLinkUrl: body.magicLinkUrl ?? null }
}

export async function signOut(allSessions = false): Promise<void> {
  await fetch(`${API_PREFIX}/api/auth/logout${allSessions ? "?all=true" : ""}`, {
    method: "POST",
    cache: "no-store",
  })
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

export interface SettingsDownloadsView {
  /** Deploy-time feature switch: wishlist downloads available at all (yt-dlp + a writable download dir). */
  enabled: boolean
  /** Runtime toggle: auto-sweep Pending wishlist items in the background. Flippable from the UI. */
  autoDownload: boolean
}

export interface SettingsResponse {
  paths: SettingsPathsView
  providers: SettingsProvidersView
  spotify: SettingsSpotifyView
  qualityGrading: SettingsQualityGradingView
  lyricsTranscription: SettingsLyricsTranscriptionView
  downloads: SettingsDownloadsView
  updatedAtUtc: string | null
}

export interface SettingsUpdateRequest {
  providers?: Partial<SettingsProvidersView>
  qualityGrading?: { enabled?: boolean }
  downloads?: { autoDownload?: boolean }
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

// ── Duplicates (ambiguous fingerprint clusters) ────────────────────────────────

/** One file inside a duplicate cluster (a "loser" copy flagged as IsDuplicate). */
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
  /** Server-computed keep-priority (FLAC/WAV/AIFF rank above bitrate). */
  qualityScore: number
}

/** The "kept" file the cluster's duplicates point at (the auto-resolver's pick). */
export interface DuplicateBest {
  id: number
  sourcePath: string
  fileName: string
  extension?: string | null
  fileSizeBytes: number
  artist?: string | null
  album?: string | null
  title?: string | null
  bitrate?: number | null
  fingerprint?: string | null
  qualityScore: number
}

export interface DuplicateGroup {
  fingerprint: string | null
  /** The kept copy; null when no DuplicateOfId was recorded for the cluster. */
  best: DuplicateBest | null
  duplicates: DuplicateMember[]
}

export interface DuplicatesResponse {
  totalDuplicates: number
  groups: number
  duplicateGroups: DuplicateGroup[]
}

/** All tracks flagged as duplicates, grouped by fingerprint (read-only — no resolve endpoint yet). */
export async function fetchDuplicates(): Promise<DuplicatesResponse> {
  return requestJson<DuplicatesResponse>("/api/library/duplicates")
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
