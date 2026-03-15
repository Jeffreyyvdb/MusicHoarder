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
  enrichmentStatus: "pending" | "processing" | "complete" | "failed"
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
