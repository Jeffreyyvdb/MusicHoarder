export type FileType = "folder" | "audio"

export interface FileItem {
  id: string
  name: string
  type: FileType
  path: string
  parentId: string | null
  children?: FileItem[]
  metadata?: TrackMetadata
}

export interface TrackMetadata {
  title: string
  artist: string
  album: string
  year: number
  genre: string
  duration: number
  bitrate: number
  sampleRate: number
  format: string
  fileSize: number
  albumArt?: string
  lyrics?: string
  fingerprint?: string
  enrichmentStatus: "pending" | "processing" | "complete" | "failed" | "needsreview"
  sources: {
    musicbrainz?: boolean
    lastfm?: boolean
    spotify?: boolean
    genius?: boolean
  }
}

export interface BreadcrumbItem {
  id: string
  name: string
  path: string
}

export type ImportStatus = "discovered" | "fingerprinting" | "enriching" | "copying" | "review" | "complete" | "failed"

export interface ImportJob {
  id: string
  sourcePath: string
  destinationPath: string
  status: "running" | "paused" | "completed"
  startedAt: Date
  tracksDiscovered: number
  tracksProcessed: number
  tracksCopied: number
  tracksReview: number
  tracksFailed: number
}

export interface TrackImport {
  id: string
  originalPath: string
  destinationPath?: string
  status: ImportStatus
  metadata?: TrackMetadata
  suggestedMetadata?: Partial<TrackMetadata>
  issues?: string[]
  fingerprint?: string
}

// Artist Discography Types
export interface Artist {
  id: string
  name: string
  image?: string
  genres: string[]
  albumsInLibrary: number
  totalAlbums: number
  tracksInLibrary: number
  totalTracks: number
}

export interface DiscographyAlbum {
  id: string
  name: string
  year: number
  albumArt?: string
  type: "album" | "single" | "ep" | "compilation"
  tracks: DiscographyTrack[]
  inLibrary: boolean
  tracksOwned: number
  totalTracks: number
}

export interface DiscographyTrack {
  id: string
  name: string
  trackNumber: number
  duration: number
  inLibrary: boolean
  downloadStatus?: "available" | "downloading" | "downloaded" | "unavailable"
}

// Spotify Sync Types
export interface SpotifyPlaylist {
  id: string
  name: string
  description?: string
  image?: string
  trackCount: number
  owner: string
  syncStatus: "not_synced" | "syncing" | "synced" | "partial"
  tracksMatched: number
  lastSynced?: Date
}

export interface SpotifyTrack {
  id: string
  name: string
  artist: string
  album: string
  albumArt?: string
  duration: number
  inLibrary: boolean
  matchConfidence?: number
}
