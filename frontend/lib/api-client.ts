import type { FileItem } from "@/lib/types"

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

export interface ApiSong {
  id: number
  sourcePath: string
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
  enrichmentStatus?: string | null
}

interface SongsResponse {
  count: number
  includeDeleted: boolean
  songs: ApiSong[]
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
    throw new Error(`Request failed for ${path}: ${response.status}`)
  }

  return (await response.json()) as T
}

function mapEnrichmentStatus(status?: string | null): "pending" | "processing" | "complete" | "failed" {
  const normalized = status?.toLowerCase()
  if (normalized === "failed") return "failed"
  if (normalized === "matched" || normalized === "complete") return "complete"
  if (normalized === "running" || normalized === "processing") return "processing"
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

export function buildFileSystemFromSongs(songs: ApiSong[]): FileItem[] {
  const root: FileItem = {
    id: "root",
    name: "Music Library",
    type: "folder",
    path: "/Music Library",
    parentId: null,
    children: [],
  }

  for (const song of songs) {
    const normalizedPath = (song.sourcePath || "").replace(/\\/g, "/").replace(/\/+/g, "/")
    const pathSegments = normalizedPath.split("/").filter(Boolean)
    const fileName = song.fileName || pathSegments[pathSegments.length - 1] || `track-${song.id}`
    const folderSegments = pathSegments.slice(0, Math.max(pathSegments.length - 1, 0))

    let currentFolder = root
    for (const segment of folderSegments) {
      currentFolder = getOrCreateChildFolder(currentFolder, segment)
    }

    const metadataTitle = song.title?.trim() || fileName.replace(/\.[^.]+$/, "")
    const metadataArtist = song.artist?.trim() || "Unknown Artist"
    const metadataAlbum = song.album?.trim() || "Unknown Album"

    const audioFile: FileItem = {
      id: `song:${song.id}`,
      name: fileName,
      type: "audio",
      path: normalizedPath || `${currentFolder.path}/${fileName}`,
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
  return requestJson<ApiStats>("/stats")
}

export async function fetchSongs(includeDeleted = false): Promise<ApiSong[]> {
  const result = await requestJson<SongsResponse>(`/songs?includeDeleted=${includeDeleted}`)
  return result.songs ?? []
}

export async function startScan(): Promise<{ scanId: string }> {
  return requestJson<{ scanId: string }>("/scan", { method: "POST" })
}
