import type { FileItem } from "@/lib/types"
import { isDemoMode } from "@/lib/app-mode"
import { mockFileSystem, mockImportJob } from "@/lib/mock-data"
import {
  getDemoSpotifyCredentials,
  getDemoSpotifyDisconnectMessage,
  getDemoSpotifyLikedSongs,
  getDemoSpotifyPlaylistTracks,
  getDemoSpotifyPlaylists,
  getDemoSpotifySaveCredentialsMessage,
  getDemoSpotifyStatus,
} from "@/lib/mock-spotify-api"

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

const SOURCE_ROOT = "/Volumes/music"
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
  const realSongs = audioFiles.map((file, index) => {
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
      lyricsStatus: file.metadata?.lyricsStatus ?? null,
      hasSyncedLyrics: file.metadata?.hasSyncedLyrics ?? null,
      hasPlainLyrics: file.metadata?.hasPlainLyrics ?? null,
      isInstrumental: file.metadata?.isInstrumental ?? null,
      syncedLyrics: file.metadata?.syncedLyrics ?? null,
      plainLyrics: file.metadata?.plainLyrics ?? null,
      sampleRate: file.metadata?.sampleRate ?? null,
      bitRate: file.metadata?.bitrate ?? null,
    }
  })
  const totalFromReal = realSongs.reduce((sum, s) => sum + s.fileSizeBytes, 0)
  const synthetic = buildSyntheticDemoSongs(realSongs.length + 1, totalFromReal)
  return [...realSongs, ...synthetic]
}

/** Target total demo library size in bytes (~110 GB) for a MusicHoarder-style library. */
const DEMO_TOTAL_BYTES_TARGET = 110 * 1024 * 1024 * 1024
/** Number of synthetic tracks to add so the demo feels like a large library. */
const DEMO_SYNTHETIC_TRACK_COUNT = 2100

/** Messy top-level source folders (like real hoarded sources: downloads, rips, backups). */
const MESSY_SOURCE_ROOTS = [
  "Downloads",
  "CD Rips",
  "Music Backup",
  "New Folder",
  "Import 2023",
  "From Phone",
  "Rips",
  "Unsorted",
  "Music (1)",
  "Transfer 2022",
  "My Music",
  "random",
  "2022 Import",
  "Old Library",
  "iTunes Export",
  "Spotify Export",
  "Backup Jan",
]

/** Messy subfolder patterns: date folders, disc folders, duplicates, inconsistent casing. */
const MESSY_SUBFOLDERS = [
  "2022-11",
  "2020-03",
  "2023-01",
  "Disc 1",
  "disc2",
  "Disc1",
  "Album",
  "Tracks",
  " (1)",
  " - copy",
  "",
]

function buildMessySourcePath(
  index: number,
  artist: string,
  album: string,
  fileName: string
): string {
  const rootIndex = index % MESSY_SOURCE_ROOTS.length
  const root = MESSY_SOURCE_ROOTS[rootIndex]
  const subIndex = (index >> 4) % MESSY_SUBFOLDERS.length
  const sub = MESSY_SUBFOLDERS[subIndex]

  // Mix: some under artist-like folders (but messy), some under date/random folders
  const useArtistFolder = index % 3 !== 0
  if (useArtistFolder) {
    // Inconsistent casing / naming: sometimes lowercase, sometimes "Artist (1)"
    const artistFolder =
      index % 5 === 0
        ? artist.toLowerCase()
        : index % 7 === 0
          ? `${artist} (1)`
          : index % 11 === 0
            ? artist.replace(/\s/g, "_")
            : artist
    const base = `${SOURCE_ROOT}/${root}/${artistFolder}`
    if (sub) {
      const albumFolder = sub.startsWith(" ") ? `${album}${sub}` : sub
      return `${base}/${albumFolder}/${fileName}`
    }
    return `${base}/${album}/${fileName}`
  }

  // No artist folder: e.g. Downloads/2022-11/01 - Track 1.flac or Unsorted/track_01.mp3
  const base = `${SOURCE_ROOT}/${root}`
  if (sub) return `${base}/${sub}/${fileName}`
  return `${base}/${fileName}`
}

const DEMO_SYNCED_LYRICS_POOL = [
  `[00:00.00] Sunrise on the avenue\n[00:04.50] Colors bleeding through the haze\n[00:09.00] Every step I take is new\n[00:13.50] Walking through these golden days\n[00:18.00] \n[00:19.50] Turn the dial and find a sound\n[00:24.00] Let the melody unwind\n[00:28.50] Echoes spinning all around\n[00:33.00] Leaving yesterday behind`,
  `[00:05.00] Neon lights and midnight rain\n[00:09.50] Driving down an empty lane\n[00:14.00] Radio is on again\n[00:18.50] Singing through the windowpane\n[00:23.00] \n[00:24.50] We don't need a masterplan\n[00:29.00] Just the road beneath our feet\n[00:33.50] Hand in hand we understand\n[00:38.00] Every stranger that we meet`,
  `[00:02.00] Woke up to a silent room\n[00:06.50] Shadows dancing on the wall\n[00:11.00] Petals from a wilted bloom\n[00:15.50] Scattered down the empty hall\n[00:20.00] \n[00:21.50] Time moves slow when you're alone\n[00:26.00] Counting cracks along the floor\n[00:30.50] Dialing on a broken phone\n[00:35.00] Waiting for a knock at the door`,
  `[00:08.00] Electric blue and cherry red\n[00:12.50] Frequencies inside my head\n[00:17.00] Dancing on a laser thread\n[00:21.50] Follow where the signal led\n[00:26.00] \n[00:27.50] Bass drop hits and walls collide\n[00:32.00] Neon pulses in my chest\n[00:36.50] Nothing left for us to hide\n[00:41.00] This is how we pass the test`,
  `[00:03.00] Morning fog across the bay\n[00:07.50] Coffee steam and yesterday\n[00:12.00] Pages turn and drift away\n[00:16.50] Words I never thought I'd say\n[00:21.00] \n[00:22.50] Take my hand and close your eyes\n[00:27.00] Feel the earth beneath your skin\n[00:31.50] Underneath these open skies\n[00:36.00] Let the healing now begin`,
  `[00:10.00] City hum and traffic flow\n[00:14.50] Skyscrapers begin to glow\n[00:19.00] Underground where rivers go\n[00:23.50] Secrets that the streets all know\n[00:28.00] \n[00:29.50] Subway doors slide open wide\n[00:34.00] Faces lost in passing light\n[00:38.50] Everyone has wounds inside\n[00:43.00] Holding on to make it right`,
  `[00:00.00] Starlight falling like the rain\n[00:04.50] Catching fire on the plain\n[00:09.00] Every ending starts again\n[00:13.50] Joy is born from letting go of pain\n[00:18.00] \n[00:19.50] Open up the rusted gate\n[00:24.00] Futures bloom from broken ground\n[00:28.50] It is never, ever, too late\n[00:33.00] To become what you have found`,
  `[00:06.00] Velvet sky at half past nine\n[00:10.50] Satellites begin to shine\n[00:15.00] Tracing out a dotted line\n[00:19.50] Connecting your world to mine\n[00:24.00] \n[00:25.50] Waves crash on a distant shore\n[00:30.00] Carrying the songs we wrote\n[00:34.50] Every tide reveals once more\n[00:39.00] Messages sealed inside a note`,
  `[00:04.00] Dusty roads and summer heat\n[00:08.50] Gravel crunching under feet\n[00:13.00] Lemonade on repeat\n[00:17.50] Lazy days are bittersweet\n[00:22.00] \n[00:23.50] Fireflies at half past eight\n[00:28.00] Porch light glowing amber gold\n[00:32.50] Stories that we'd annotate\n[00:37.00] Memories that never get old`,
  `[00:07.00] Pixel hearts on glowing screens\n[00:11.50] We are more than what it seems\n[00:16.00] Living somewhere in between\n[00:20.50] Reality and lucid dreams\n[00:25.00] \n[00:26.50] Upload all your fears tonight\n[00:31.00] Download courage, press restart\n[00:35.50] Every bug becomes a light\n[00:40.00] Code that's written from the heart`,
]

function demoSyncedLyricsForIndex(i: number): string {
  return DEMO_SYNCED_LYRICS_POOL[i % DEMO_SYNCED_LYRICS_POOL.length]
}

function demoPlainLyricsFromSynced(synced: string): string {
  return synced
    .split("\n")
    .map((line) => line.replace(/^\[\d{2}:\d{2}\.\d{2,3}\]\s*/, ""))
    .filter((line) => line.trim().length > 0)
    .join("\n")
}

function buildSyntheticDemoSongs(startId: number, totalBytesFromReal: number): ApiSong[] {
  const bytesRemaining = Math.max(0, DEMO_TOTAL_BYTES_TARGET - totalBytesFromReal)
  const bytesPerTrack = Math.floor(bytesRemaining / DEMO_SYNTHETIC_TRACK_COUNT)
  const artists = [
    "Arctic Monkeys", "Tame Impala", "Tyler the Creator", "Kendrick Lamar", "Billie Eilish",
    "Lana Del Rey", "The Strokes", "MGMT", "Gorillaz", "LCD Soundsystem", "Phoenix", "Vampire Weekend",
    "Arcade Fire", "Kanye West", "Frank Ocean", "Childish Gambino", "Anderson .Paak", "Mac DeMarco",
  ]
  const synthetic: ApiSong[] = []
  for (let i = 0; i < DEMO_SYNTHETIC_TRACK_COUNT; i++) {
    const id = startId + i
    const artist = artists[i % artists.length]
    const albumNum = Math.floor(i / artists.length) % 20 + 1
    const trackNum = (i % 12) + 1
    const album = `Album ${albumNum}`
    const title = `Track ${trackNum}`
    const ext = i % 3 === 0 ? "mp3" : "flac"
    const cleanFileName = `${String(trackNum).padStart(2, "0")} - ${title}.${ext}`
    const messyFileName =
      i % 6 === 0 ? `track_${String(trackNum).padStart(2, "0")}.${ext}` : cleanFileName
    const sourcePath = buildMessySourcePath(i, artist, album, messyFileName)
    const destArtist = artist.replace(/[/\\]/g, "").trim() || "Unknown"
    const destAlbum = album.replace(/[/\\]/g, "").trim() || "Unknown Album"
    const destinationPath = `${DESTINATION_ROOT}/${destArtist}/${destAlbum}/${cleanFileName}`
    const fileSizeBytes = bytesPerTrack + (i % 5) * 1024 * 1024
    const synced = demoSyncedLyricsForIndex(i)
    const plain = demoPlainLyricsFromSynced(synced)
    synthetic.push({
      id,
      sourcePath,
      destinationPath,
      fileName: cleanFileName,
      extension: `.${ext}`,
      fileSizeBytes,
      artist,
      album,
      title,
      year: 2010 + (i % 15),
      durationSeconds: 180 + (i % 240),
      fingerprint: null,
      musicBrainzId: null,
      spotifyId: null,
      enrichmentStatus: i % 5 === 0 ? "pending" : "complete",
      lyricsStatus: "Fetched",
      hasSyncedLyrics: true,
      hasPlainLyrics: true,
      isInstrumental: false,
      syncedLyrics: synced,
      plainLyrics: plain,
      sampleRate: ext === "flac" ? 44100 : 48000,
      bitRate: ext === "flac" ? 1411 : 320,
    })
  }
  return synthetic
}

function mapEnrichmentStatus(status?: string | number | null): "pending" | "processing" | "complete" | "failed" | "needsreview" {
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
        lyricsStatus: (song.lyricsStatus ?? "NotFetched") as import("@/lib/types").LyricsStatus,
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
    const fingerprintedCount =
      demoSongs.filter((s) => Boolean(s.fingerprint)).length ||
      Math.floor(demoSongs.length * 0.9)
    const enrichedCount =
      demoSongs.filter((s) => s.enrichmentStatus === "complete").length ||
      Math.floor(demoSongs.length * 0.76)
    return {
      sourcePath: mockImportJob.sourcePath,
      destinationPath: mockImportJob.destinationPath,
      scan: null,
      job: {
        status: "running",
        startedAt: new Date(Date.now() - 45 * 60 * 1000).toISOString(),
        tracksDiscovered: demoSongs.length,
        tracksProcessed: demoSongs.length,
        tracksFingerprinted: fingerprintedCount,
        tracksEnriched: enrichedCount,
        tracksBuildEligible: Math.min(enrichedCount, Math.max(copiedCount, Math.floor(demoSongs.length * 0.6))),
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
  if (isDemoMode) return { ok: true, jobId: `demo-scan-${Date.now()}` }
  return triggerEnrichmentJob("/api/enrichment/scan")
}

export async function triggerFingerprint(): Promise<EnrichmentTriggerResult> {
  if (isDemoMode) return { ok: true, jobId: `demo-fp-${Date.now()}` }
  return triggerEnrichmentJob("/api/enrichment/fingerprint")
}

export async function triggerEnrich(): Promise<EnrichmentTriggerResult> {
  if (isDemoMode) return { ok: true, jobId: `demo-enrich-${Date.now()}` }
  return triggerEnrichmentJob("/api/enrichment/enrich")
}

export async function triggerBuild(): Promise<EnrichmentTriggerResult> {
  if (isDemoMode) return { ok: true, jobId: `demo-build-${Date.now()}` }
  return triggerEnrichmentJob("/api/enrichment/build")
}

export async function cancelJob(): Promise<{ message: string }> {
  if (isDemoMode) return { message: "No job is currently running." }
  return requestJson<{ message: string }>("/api/enrichment/cancel", { method: "POST" })
}

export async function pauseStep(step: string): Promise<{ message: string }> {
  if (isDemoMode) return { message: `${step} paused.` }
  return requestJson<{ message: string }>(`/api/enrichment/pause?step=${step}`, { method: "POST" })
}

export async function resumeStep(step: string): Promise<{ message: string }> {
  if (isDemoMode) return { message: `${step} resumed.` }
  return requestJson<{ message: string }>(`/api/enrichment/resume?step=${step}`, { method: "POST" })
}

export async function fetchJobStatus(): Promise<JobStatusResponse> {
  return requestJson<JobStatusResponse>("/api/enrichment/status")
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
  if (isDemoMode) {
    const demoSongs = buildDemoSongs()
    const song = demoSongs.find((s) => s.id === trackId)
    if (song && (song.syncedLyrics ?? song.plainLyrics)) {
      return {
        id: trackId,
        lyricsStatus: song.lyricsStatus ?? "Fetched",
        isInstrumental: song.isInstrumental ?? undefined,
        synced: song.syncedLyrics ?? null,
        plain: song.plainLyrics ?? null,
      }
    }
    return { id: trackId, lyricsStatus: "NotFound", synced: null, plain: null }
  }
  return requestJson<TrackLyricsResponse>(`/api/tracks/${trackId}/lyrics`)
}

export function getSongStreamUrl(songId: number): string {
  if (isDemoMode) return "/demo-audio.mp3"
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
  if (isDemoMode) return getDemoSpotifyStatus()
  return requestJson<SpotifyStatusResponse>("/api/spotify/status")
}

export async function fetchSpotifyConnectUrl(): Promise<SpotifyConnectResponse> {
  if (isDemoMode) {
    throw new Error("Spotify login is not available in demo mode. Data shown is sample content only.")
  }
  return requestJson<SpotifyConnectResponse>("/api/spotify/connect")
}

export async function disconnectSpotify(): Promise<{ message: string }> {
  if (isDemoMode) return getDemoSpotifyDisconnectMessage()
  return requestJson<{ message: string }>("/api/spotify/disconnect", { method: "DELETE" })
}

export async function fetchSpotifyCredentials(): Promise<SpotifyCredentialsResponse> {
  if (isDemoMode) return getDemoSpotifyCredentials()
  return requestJson<SpotifyCredentialsResponse>("/api/spotify/credentials")
}

export async function saveSpotifyCredentials(clientId: string, clientSecret: string): Promise<{ message: string }> {
  if (isDemoMode) return getDemoSpotifySaveCredentialsMessage()
  return requestJson<{ message: string }>("/api/spotify/credentials", {
    method: "PUT",
    body: JSON.stringify({ clientId, clientSecret }),
  })
}

export async function fetchSpotifyLikedSongs(offset = 0, limit = 50): Promise<SpotifyLikedSongsApiResponse> {
  if (isDemoMode) return getDemoSpotifyLikedSongs(offset, limit)
  return requestJson<SpotifyLikedSongsApiResponse>(`/api/spotify/liked-songs?offset=${offset}&limit=${limit}`)
}

export async function fetchSpotifyPlaylists(): Promise<SpotifyPlaylistsApiResponse> {
  if (isDemoMode) return getDemoSpotifyPlaylists()
  return requestJson<SpotifyPlaylistsApiResponse>("/api/spotify/playlists")
}

export async function fetchSpotifyPlaylistTracks(playlistId: string, offset = 0, limit = 50): Promise<SpotifyPlaylistTracksApiResponse> {
  if (isDemoMode) return getDemoSpotifyPlaylistTracks(playlistId, offset, limit)
  return requestJson<SpotifyPlaylistTracksApiResponse>(
    `/api/spotify/playlists/${playlistId}/tracks?offset=${offset}&limit=${limit}`
  )
}

export async function fetchReviewTracks(): Promise<ApiSong[]> {
  if (isDemoMode) {
    const demoSongs = buildDemoSongs()
    return demoSongs.filter(
      (s) =>
        mapEnrichmentStatus(s.enrichmentStatus) === "needsreview"
    )
  }

  const result = await requestJson<SongsResponse>(
    "/songs?enrichmentStatus=needsreview"
  )
  return result.songs ?? []
}
