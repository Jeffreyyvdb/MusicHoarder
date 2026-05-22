import type { FileItem } from "$lib/types"
import { createPasskey, getPasskeyAssertion } from "$lib/webauthn-client"

const API_PREFIX = "/api/mh"
const INFRASTRUCTURE_PREFIX_SEGMENT = /^(volumes?|mnt|media|srv|share|shares|nas|data|storage|music|musichoarder|library|source|destination|users|home|tmp)$/i

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
  albumArtist?: string | null
  album?: string | null
  title?: string | null
  year?: number | null
  trackNumber?: number | null
  durationSeconds?: number | null
  fingerprint?: string | null
  musicBrainzId?: string | null
  musicBrainzReleaseId?: string | null
  spotifyId?: string | null
  acoustIdTrackId?: string | null
  lrclibId?: string | null
  enrichmentStatus?: string | number | null
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
  /** Sample rate in Hz (e.g. 44100). Shown in track details when present. */
  sampleRate?: number | null
  /** Bitrate in kbps (e.g. 320, 1411). Shown in track details when present. */
  bitRate?: number | null
  /** Optional album cover URL. The backend currently leaves this unset and the UI
   * falls back to an initials tile. */
  albumArt?: string | null
}

interface SongsResponse {
  count: number
  includeDeleted: boolean
  songs: ApiSong[]
}

export type LibraryPathMode = "destination" | "source"

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
    try {
      const body = await response.json() as Record<string, unknown>
      detail = (body.message as string) ?? JSON.stringify(body)
    } catch {
      // ignore parse errors
    }
    throw new Error(detail || `Request failed for ${path}: ${response.status}`)
  }

  return (await response.json()) as T
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

function createFolder(parent: FileItem, folderName: string): FileItem {
  const parentPath = parent.path === "/" ? "" : parent.path
  return {
    id: `folder:${parentPath}/${folderName}`,
    name: folderName,
    type: "folder",
    path: `${parentPath}/${folderName}`.replace("//", "/"),
    parentId: parent.id,
    children: [],
  }
}

function getOrCreateChildFolder(parent: FileItem, folderName: string): FileItem {
  const existingFolder = parent.children?.find(
    (child) => child.type === "folder" && child.name === folderName
  )
  if (existingFolder) {
    return existingFolder
  }

  const folder = createFolder(parent, folderName)
  parent.children = [...(parent.children ?? []), folder]
  return folder
}

function normalizeSourcePath(sourcePath?: string | null): string {
  const rawPath = (sourcePath ?? "").trim()
  if (!rawPath) return ""

  const slashNormalized = rawPath.replace(/\\/g, "/").replace(/\/+/g, "/")
  const withoutWindowsDrive = slashNormalized.replace(/^[a-zA-Z]:\//, "/")
  return withoutWindowsDrive.replace(/\/+$/, "")
}

function isFileNameSegment(segment: string): boolean {
  return /\.[^/.]+$/.test(segment)
}

function getPathSegments(rawPath?: string | null): string[] {
  return normalizeSourcePath(rawPath).split("/").filter(Boolean)
}

function getFolderSegmentsFromPath(rawPath?: string | null): string[] {
  const segments = getPathSegments(rawPath)
  if (segments.length === 0) return []
  return isFileNameSegment(segments[segments.length - 1] ?? "") ? segments.slice(0, -1) : segments
}

function commonPrefixSegments(paths: string[][]): string[] {
  if (paths.length === 0) return []

  const first = paths[0]
  let end = first.length
  for (let i = 1; i < paths.length; i++) {
    const current = paths[i]
    let j = 0
    while (j < end && j < current.length && first[j] === current[j]) {
      j++
    }
    end = j
    if (end === 0) break
  }

  return first.slice(0, end)
}

function countRemovablePrefixSegments(sharedPrefix: string[]): number {
  let count = 0
  for (const segment of sharedPrefix) {
    if (/^[a-zA-Z]:$/.test(segment) || INFRASTRUCTURE_PREFIX_SEGMENT.test(segment)) {
      count++
      continue
    }
    break
  }
  return count
}

function countRemovablePrefixSegmentsForMode(
  mode: LibraryPathMode,
  sharedPrefix: string[],
  allFolderSegments: string[][]
): number {
  if (mode === "destination") {
    if (allFolderSegments.length === 0) return 0
    const shortestPathLength = Math.min(...allFolderSegments.map((segments) => segments.length))
    const maxRemovable = Math.max(0, shortestPathLength - 1)
    return Math.min(sharedPrefix.length, maxRemovable)
  }

  return countRemovablePrefixSegments(sharedPrefix)
}

function inferPathParts(song: ApiSong): {
  folderSegments: string[]
  fileName: string
  normalizedFilePath: string | null
  normalizedPathSegments: string[]
} {
  const normalizedPath = normalizeSourcePath(song.sourcePath)
  const pathSegments = normalizedPath.split("/").filter(Boolean)
  const lastSegment = pathSegments[pathSegments.length - 1] ?? ""
  const hasFileNameInPath = /\.[^/.]+$/.test(lastSegment)

  const fileNameFromApi = song.fileName?.trim()
  const inferredFromPath = hasFileNameInPath ? lastSegment : ""
  const fileName = fileNameFromApi || inferredFromPath || `track-${song.id}`

  const folderSegments = hasFileNameInPath ? pathSegments.slice(0, -1) : pathSegments
  const normalizedFilePath = hasFileNameInPath ? `/${pathSegments.join("/")}` : null

  return { folderSegments, fileName, normalizedFilePath, normalizedPathSegments: pathSegments }
}

function getSongsForMode(songs: ApiSong[], mode: LibraryPathMode): ApiSong[] {
  if (mode === "source") return songs
  return songs.filter((song) => Boolean(song.destinationPath?.trim()))
}

export function buildFileSystemFromSongs(
  songs: ApiSong[],
  mode: LibraryPathMode = "source"
): FileItem[] {
  const songsForMode = getSongsForMode(songs, mode)
  const allFolderSegments = songsForMode.map((song) =>
    getFolderSegmentsFromPath(mode === "destination" ? song.destinationPath : song.sourcePath)
  )
  const pathRootPrefixSegments = commonPrefixSegments(allFolderSegments)
  const removablePrefixCount = countRemovablePrefixSegmentsForMode(
    mode,
    pathRootPrefixSegments,
    allFolderSegments
  )

  const root: FileItem = {
    id: "root",
    name: mode === "destination" ? "Destination Library" : "Source Library",
    type: "folder",
    path: "/",
    parentId: null,
    children: [],
  }

  for (const song of songsForMode) {
    const selectedPath = mode === "destination" ? song.destinationPath : song.sourcePath
    const { folderSegments, fileName, normalizedPathSegments } = inferPathParts({
      ...song,
      sourcePath: selectedPath ?? "",
    })
    const relativeFolderSegments = folderSegments.slice(removablePrefixCount)
    const relativePathSegments = normalizedPathSegments.slice(removablePrefixCount)
    const normalizedFilePath = relativePathSegments.length > 0 ? `/${relativePathSegments.join("/")}` : null

    let currentFolder = root
    for (const segment of relativeFolderSegments) {
      currentFolder = getOrCreateChildFolder(currentFolder, segment)
    }

    const metadataTitle = song.title?.trim() || fileName.replace(/\.[^.]+$/, "")
    const metadataArtist = song.artist?.trim() || "Unknown Artist"
    const metadataAlbum = song.album?.trim() || "Unknown Album"

    const audioFile: FileItem = {
      id: `song:${song.id}`,
      name: fileName,
      type: "audio",
      path: normalizedFilePath || `${currentFolder.path}/${fileName}`,
      parentId: currentFolder.id,
      metadata: {
        title: metadataTitle,
        artist: metadataArtist,
        album: metadataAlbum,
        year: song.year ?? 0,
        genre: "Unknown",
        duration: song.durationSeconds ?? 0,
        bitrate: song.bitRate ?? 0,
        sampleRate: song.sampleRate ?? 0,
        format: (song.extension ?? "Unknown").replace(/^\./, "").toUpperCase(),
        fileSize: song.fileSizeBytes ?? 0,
        fingerprint: song.fingerprint ?? undefined,
        enrichmentStatus: mapEnrichmentStatus(song.enrichmentStatus),
        matchedBy: song.matchedBy ?? undefined,
        lyricsStatus: (song.lyricsStatus ?? "NotFetched") as import("$lib/types").LyricsStatus,
        hasSyncedLyrics: song.hasSyncedLyrics ?? false,
        hasPlainLyrics: song.hasPlainLyrics ?? false,
        isInstrumental: song.isInstrumental ?? undefined,
        syncedLyrics: song.syncedLyrics ?? undefined,
        plainLyrics: song.plainLyrics ?? undefined,
        lyrics: song.syncedLyrics ?? song.plainLyrics ?? undefined,
        sources: {
          musicbrainz: Boolean(song.musicBrainzId),
          spotify: Boolean(song.spotifyId),
        },
        sourceIds: {
          musicBrainzId: song.musicBrainzId ?? undefined,
          musicBrainzReleaseId: song.musicBrainzReleaseId ?? undefined,
          spotifyId: song.spotifyId ?? undefined,
          acoustIdTrackId: song.acoustIdTrackId ?? undefined,
          lrclibId: song.lrclibId ?? undefined,
        },
      },
    }

    currentFolder.children = [...(currentFolder.children ?? []), audioFile]
  }

  return [root]
}

// ── Album grouping ────────────────────────────────────────────────────────────

const UNKNOWN_ALBUM = "Unknown Album"
const UNKNOWN_ARTIST = "Unknown Artist"

/** Aggregated view of all songs sharing an `(albumArtist, album)` pair. */
export interface AlbumSummary {
  /** Stable key — `${artistLower}::${titleLower}`. Matches the `?album=` URL param the existing UI emits. */
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
  /** First non-null musicBrainzReleaseId encountered. */
  musicBrainzReleaseId: string | null
  /** First non-null albumArt URL encountered. */
  coverUrl: string | null
  /** Songs ordered by track number then title. */
  songs: ApiSong[]
}

function nonEmpty(value: string | null | undefined): string | null {
  const trimmed = (value ?? "").trim()
  return trimmed.length > 0 ? trimmed : null
}

/**
 * Group raw songs into album summaries used by Gallery / AlbumPage.
 * Same key shape as the previous AlbumGridView (artistLower::titleLower) so
 * existing `?album=` URLs keep working across the migration.
 */
export function buildAlbumsFromSongs(songs: ApiSong[]): AlbumSummary[] {
  const map = new Map<string, AlbumSummary>()
  for (const song of songs) {
    const title = nonEmpty(song.album) ?? UNKNOWN_ALBUM
    const artist = nonEmpty(song.albumArtist) ?? nonEmpty(song.artist) ?? UNKNOWN_ARTIST
    const key = `${artist.toLowerCase()}::${title.toLowerCase()}`
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
        musicBrainzReleaseId: null,
        coverUrl: null,
        songs: [],
      }
      map.set(key, entry)
    }
    entry.trackCount += 1
    entry.durationSeconds += song.durationSeconds ?? 0
    entry.byteSize += song.fileSizeBytes ?? 0
    if (song.year && (!entry.year || song.year < entry.year)) entry.year = song.year
    if (!entry.musicBrainzReleaseId && song.musicBrainzReleaseId) {
      entry.musicBrainzReleaseId = song.musicBrainzReleaseId
    }
    if (!entry.coverUrl && song.albumArt) entry.coverUrl = song.albumArt
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

function albumKeyOf(song: ApiSong): string {
  const title = nonEmpty(song.album) ?? UNKNOWN_ALBUM
  const artist = nonEmpty(song.albumArtist) ?? nonEmpty(song.artist) ?? UNKNOWN_ARTIST
  return `${artist.toLowerCase()}::${title.toLowerCase()}`
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

/** Group songs by `(albumArtist ?? artist ?? "Unknown Artist")`, sorted alphabetically. */
export function buildArtistGroups(songs: ApiSong[]): GroupSummary[] {
  const map = new Map<string, GroupAccumulator>()
  for (const song of songs) {
    const label = nonEmpty(song.albumArtist) ?? nonEmpty(song.artist) ?? UNKNOWN_ARTIST
    const key = label.toLowerCase()
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
    if (!entry.coverUrl && song.albumArt) entry.coverUrl = song.albumArt
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
    if (!entry.coverUrl && song.albumArt) entry.coverUrl = song.albumArt
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
  children: DirectoryMatchNode[]
}

export async function fetchDirectoryMatchTree(): Promise<DirectoryMatchNode> {
  return requestJson<DirectoryMatchNode>("/library/directory-tree")
}

// ── Ingest runs (history) ─────────────────────────────────────────────────────

export type ApiRunStatus = "running" | "completed" | "cancelled" | "failed"

export interface ApiRun {
  id: string
  status: ApiRunStatus
  startedAtUtc: string
  endedAtUtc?: string | null
  sourcePath: string
  destinationPath: string
  tracksDiscovered: number
  tracksProcessed: number
  tracksFingerprinted: number
  tracksEnriched: number
  tracksCopied: number
  tracksReview: number
  tracksFailed: number
  throughputPerSec: number
  durationSeconds?: number | null
}

export interface ApiRunLogLine {
  id: string
  type: ApiOverviewActivity["type"]
  track: string
  artist: string
  time: string
}

export interface ApiRunDetail extends ApiRun {
  logTail: ApiRunLogLine[] | null
}

export async function fetchRuns(): Promise<ApiRun[]> {
  return requestJson<ApiRun[]>("/runs")
}

export async function fetchRun(id: string): Promise<ApiRunDetail | null> {
  try {
    return await requestJson<ApiRunDetail>(`/runs/${id}`)
  } catch {
    return null
  }
}

export async function fetchSongs(includeDeleted = false): Promise<ApiSong[]> {
  const result = await requestJson<SongsResponse>(`/songs?includeDeleted=${includeDeleted}`)
  return result.songs ?? []
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
  built: number
  failed: number
  scan: StepSnapshot
  fingerprint: StepSnapshot
  enrich: StepSnapshot
  build: StepSnapshot
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

  es.onmessage = (event) => {
    try {
      onSnapshot(JSON.parse(event.data as string) as ProgressSnapshot)
    } catch {
      // Ignore parse errors
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

export interface TrackLyricsResponse {
  id: number
  lyricsStatus: string
  isInstrumental?: boolean | null
  synced?: string | null
  plain?: string | null
}

export async function fetchTrackLyrics(trackId: number): Promise<TrackLyricsResponse> {
  return requestJson<TrackLyricsResponse>(`/api/tracks/${trackId}/lyrics`)
}

export function getSongStreamUrl(songId: number): string {
  return `${API_PREFIX}/songs/${songId}/stream`
}

export function parseSongId(fileItemId: string): number | null {
  if (!fileItemId.startsWith("song:")) return null
  const parsed = Number(fileItemId.slice(5))
  return Number.isFinite(parsed) ? parsed : null
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

export interface SettingsPipelineView {
  spotifyApiMatchedThreshold: number
  acoustIdScoreThreshold: number
  enrichmentWorkerConcurrency: number
  libraryBuilderWorkerConcurrency: number
}

export interface SettingsSpotifyView {
  oAuthRedirectBaseUrl: string
  scopes: string[]
}

export interface SettingsResponse {
  paths: SettingsPathsView
  providers: SettingsProvidersView
  pipeline: SettingsPipelineView
  spotify: SettingsSpotifyView
  updatedAtUtc: string | null
}

export interface SettingsUpdateRequest {
  providers?: Partial<SettingsProvidersView>
  pipeline?: Partial<SettingsPipelineView>
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
  candidate: EnrichmentCandidate | null
}

export interface EnrichmentDetail {
  id: number
  fileName: string
  sourcePath: string
  destinationPath?: string | null
  enrichmentStatus: string
  isManuallyApproved?: boolean
  matchedBy?: string | null
  matchConfidence?: number | null
  matchWarnings?: string[] | null
  enrichmentError?: string | null
  originalMetadataCaptured: boolean
  source: EnrichmentCandidate | null
  current: EnrichmentCandidate
  providerAttempts: ProviderAttempt[]
}

export async function fetchEnrichmentDetail(songId: number): Promise<EnrichmentDetail> {
  return requestJson<EnrichmentDetail>(`/songs/${songId}/enrichment-detail`)
}
