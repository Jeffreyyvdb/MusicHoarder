import type { FileItem } from "@/lib/types"
import { isDemoMode } from "@/lib/app-mode"
import { mockFileSystem, mockImportJob } from "@/lib/mock-data"

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
  album?: string | null
  title?: string | null
  year?: number | null
  durationSeconds?: number | null
  fingerprint?: string | null
  musicBrainzId?: string | null
  spotifyId?: string | null
  enrichmentStatus?: string | number | null
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
    throw new Error(`Request failed for ${path}: ${response.status}`)
  }

  return (await response.json()) as T
}

function flattenAudioFiles(items: FileItem[]): FileItem[] {
  const audioFiles: FileItem[] = []
  const stack = [...items]

  while (stack.length > 0) {
    const item = stack.pop()
    if (!item) continue

    if (item.type === "audio") {
      audioFiles.push(item)
      continue
    }

    if (item.children?.length) {
      stack.push(...item.children)
    }
  }

  return audioFiles
}

function deriveExtension(fileName: string): string | null {
  const lastDot = fileName.lastIndexOf(".")
  if (lastDot < 0 || lastDot === fileName.length - 1) return null
  return fileName.slice(lastDot)
}

const DESTINATION_ROOT = "/Music Library"

function safePathSegment(segment: string): string {
  return segment.replace(/[/\\]/g, "").trim() || "Unknown"
}

function buildDemoDestinationPath(
  fileName: string,
  artist?: string | null,
  album?: string | null
): string {
  const artistSegment = artist ? safePathSegment(artist) : "Unknown"
  const albumSegment = album ? safePathSegment(album) : "Unknown Album"
  return `${DESTINATION_ROOT}/${artistSegment}/${albumSegment}/${fileName}`
}

function buildDemoSongs(): ApiSong[] {
  const audioFiles = flattenAudioFiles(mockFileSystem)

  return audioFiles.map((file, index) => {
    const extension = deriveExtension(file.name)
    const artist = file.metadata?.artist ?? null
    const album = file.metadata?.album ?? null
    const hasUsableMetadata =
      artist && album && artist !== "Unknown Artist" && album !== "Unknown Album" && album !== "Unknown"
    const destinationPath = hasUsableMetadata
      ? buildDemoDestinationPath(file.name, artist, album)
      : `${DESTINATION_ROOT}/Unknown/${file.name}`

    return {
      id: index + 1,
      sourcePath: file.path,
      destinationPath,
      fileName: file.name,
      extension,
      fileSizeBytes: file.metadata?.fileSize ?? 0,
      artist,
      album,
      title: file.metadata?.title ?? null,
      year: file.metadata?.year ?? null,
      durationSeconds: file.metadata?.duration ?? null,
      fingerprint: file.metadata?.fingerprint ?? null,
      musicBrainzId: null,
      spotifyId: null,
      enrichmentStatus: file.metadata?.enrichmentStatus ?? null,
    }
  })
}

function mapEnrichmentStatus(status?: string | number | null): "pending" | "processing" | "complete" | "failed" {
  if (typeof status === "number") {
    switch (status) {
      case 1:
        return "complete"
      case 2:
        return "processing"
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
    if (normalized === "running" || normalized === "processing" || normalized === "needsreview") {
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
        bitrate: 0,
        sampleRate: 0,
        format: (song.extension ?? "Unknown").replace(/^\./, "").toUpperCase(),
        fileSize: song.fileSizeBytes ?? 0,
        fingerprint: song.fingerprint ?? undefined,
        enrichmentStatus: mapEnrichmentStatus(song.enrichmentStatus),
        sources: {
          musicbrainz: Boolean(song.musicBrainzId),
          spotify: Boolean(song.spotifyId),
        },
      },
    }

    currentFolder.children = [...(currentFolder.children ?? []), audioFile]
  }

  return [root]
}

export async function fetchStats(): Promise<ApiStats> {
  if (isDemoMode) {
    const demoSongs = buildDemoSongs()
    return {
      tracks: {
        total: mockImportJob.tracksDiscovered || demoSongs.length,
        deleted: 0,
      },
      storage: {
        totalBytes: demoSongs.reduce((sum, song) => sum + song.fileSizeBytes, 0),
      },
    }
  }

  return requestJson<ApiStats>("/stats")
}

export async function fetchOverview(): Promise<ApiOverview> {
  if (isDemoMode) {
    const demoSongs = buildDemoSongs()
    const copiedCount = demoSongs.filter(
      (s) => s.destinationPath && s.artist && s.artist !== "Unknown Artist"
    ).length
    return {
      sourcePath: mockImportJob.sourcePath,
      destinationPath: mockImportJob.destinationPath,
      scan: null,
      job: {
        status: "running",
        startedAt: new Date(Date.now() - 45 * 60 * 1000).toISOString(),
        tracksDiscovered: demoSongs.length,
        tracksProcessed: demoSongs.length,
        tracksCopied: copiedCount,
        tracksReview: mockImportJob.tracksReview,
        tracksFailed: mockImportJob.tracksFailed,
      },
      recentActivity: (await import("@/lib/mock-data")).mockRecentActivity.map((a) => ({
        id: a.id,
        type: a.type,
        track: a.track,
        artist: a.artist,
        time: a.time,
      })),
    }
  }

  return requestJson<ApiOverview>("/overview")
}

export async function fetchSongs(includeDeleted = false): Promise<ApiSong[]> {
  if (isDemoMode) {
    const demoSongs = buildDemoSongs()
    if (includeDeleted) return demoSongs
    return demoSongs
  }

  const result = await requestJson<SongsResponse>(`/songs?includeDeleted=${includeDeleted}`)
  return result.songs ?? []
}

export async function startScan(): Promise<{ scanId: string }> {
  if (isDemoMode) {
    return { scanId: `demo-scan-${Date.now()}` }
  }

  return requestJson<{ scanId: string }>("/scan", { method: "POST" })
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
